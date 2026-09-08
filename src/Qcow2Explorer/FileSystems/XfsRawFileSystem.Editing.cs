using System.Buffers.Binary;
using System.Text;
using Qcow2Explorer.Core;

namespace Qcow2Explorer.FileSystems;

internal sealed partial class XfsRawFileSystem
{
    private const uint BnoBtreeMagicV5 = 0x41423342;
    private const uint CountBtreeMagicV5 = 0x41423343;
    private const uint InodeBtreeMagicV5 = 0x49414233;
    private const uint FreeInodeBtreeMagicV5 = 0x46494233;
    private const uint RmapBtreeMagicV5 = 0x524d4233;
    private const ulong ReflinkInodeFlag = 0x0002;
    private const ulong BigTimeInodeFlag = 0x0008;
    private const uint FinobtFeature = 0x0001;
    private const uint RmapFeature = 0x0002;
    private const uint SupportedReadOnlyFeatures = 0x000f;
    private const uint FTypeFeature = 0x0001;
    private const uint SparseInodeFeature = 0x0002;
    private const uint BigTimeFeature = 0x0008;
    private const uint SupportedIncompatibleFeatures = FTypeFeature | SparseInodeFeature | BigTimeFeature;
    private const int ShortBtreeHeaderSizeV5 = 56;

    public bool ValidateForEditing(out string reason)
    {
        if (_writer is null)
        {
            reason = "変更を保持する書き込みオーバーレイがありません。";
            return false;
        }

        if (_superBlock.SbVersion != 5
            || (_superBlock.ReadOnlyCompatibleFeatures & (FinobtFeature | RmapFeature))
                != (FinobtFeature | RmapFeature)
            || (_superBlock.ReadOnlyCompatibleFeatures & ~SupportedReadOnlyFeatures) != 0
            || (_superBlock.IncompatibleFeatures & (FTypeFeature | SparseInodeFeature))
                != (FTypeFeature | SparseInodeFeature)
            || (_superBlock.IncompatibleFeatures & ~SupportedIncompatibleFeatures) != 0)
        {
            reason = "実験版XFS編集はv5 CRC、ftype、sparse inode、finobt、rmapbtの構成だけに対応しています。";
            return false;
        }

        if (_superBlock.QuotaFlags != 0)
        {
            reason = "quotaが有効なXFSはquota metadataも更新する必要があるため編集できません。";
            return false;
        }

        if (_superBlock.RealtimeBlocks != 0
            || _superBlock.CompatibleFeatures != 0
            || _superBlock.LogIncompatibleFeatures != 0)
        {
            reason = "realtime deviceまたは未知のv5 featureを使うXFSは編集できません。";
            return false;
        }

        if (_superBlock.InProgress)
        {
            reason = "mkfsまたはgrowfsが完了していないXFSは編集できません。";
            return false;
        }

        if (!HasCleanUnmountRecord())
        {
            reason = "clean unmount recordを確認できないXFS logは編集できません。";
            return false;
        }

        try
        {
            var super = ReadMetadata(0, _superBlock.SectorSize);
            ValidateChecksum(super, 0xe0, "XFS superblock");
            ulong freeBlocks = 0;
            ulong inodeCount = 0;
            ulong freeInodes = 0;
            for (uint ag = 0; ag < _superBlock.AgCount; ag++)
            {
                var headers = new XfsAllocationGroup(this, ag, loadTrees: false);
                freeBlocks = checked(freeBlocks + headers.FreeBlocks + headers.FreeListBlocks);
                inodeCount = checked(inodeCount + headers.InodeCount);
                freeInodes = checked(freeInodes + headers.FreeInodes);
            }

            if (EndianUtilities.ReadUInt64Big(super, 0x80) != inodeCount
                || EndianUtilities.ReadUInt64Big(super, 0x88) != freeInodes
                || EndianUtilities.ReadUInt64Big(super, 0x90) != freeBlocks)
            {
                throw new InvalidDataException("XFS superblockとallocation groupの集計値が一致しません。");
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public bool CanWriteFile(XfsNodeRef file, long contentLength, out string reason)
    {
        if (!ValidateForEditing(out reason))
        {
            return false;
        }

        if (file.Kind != XfsRawNodeKind.RegularFile || contentLength < 0)
        {
            reason = "通常ファイルと0以上のサイズを指定してください。";
            return false;
        }

        try
        {
            var inode = ReadInode(file.Inode);
            ValidateEditableRegularInode(inode);
            var oldBlocks = RequiredBlocks(inode.Length);
            var newBlocks = RequiredBlocks(checked((ulong)contentLength));
            if (oldBlocks != newBlocks)
            {
                ValidateAllocationChange(inode, newBlocks);
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void WriteFileContent(
        XfsNodeRef file,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!CanWriteFile(file, contentLength, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        var inode = ReadInode(file.Inode);
        var oldBlocks = RequiredBlocks(inode.Length);
        var newBlocks = RequiredBlocks(checked((ulong)contentLength));
        if (oldBlocks == newBlocks)
        {
            WriteToExtents(ValidateFullyAllocated(inode), content, contentLength, cancellationToken);
            WriteInodeSize(inode, checked((ulong)contentLength));
            FinishMutation();
            return;
        }

        var oldExtent = GetSingleOwnedExtent(inode);
        var agNumber = GetInodeAllocationGroup(inode.Number);
        var allocationGroup = new XfsAllocationGroup(this, agNumber, loadTrees: true);
        var newAgBlock = newBlocks == 0 ? 0U : allocationGroup.AllocateBlocks(checked((uint)newBlocks));
        var newFsBlock = newBlocks == 0 ? 0UL : ToFileSystemBlock(agNumber, newAgBlock);
        WriteContiguousContent(newFsBlock, checked((uint)newBlocks), content, contentLength, cancellationToken);
        if (newBlocks > 0)
        {
            allocationGroup.AddRmap(newAgBlock, checked((uint)newBlocks), inode.Number, 0);
        }

        if (oldExtent is not null)
        {
            var oldAgBlock = ToAllocationGroupBlock(agNumber, oldExtent.StartBlock);
            allocationGroup.RemoveRmap(oldAgBlock, oldExtent.BlockCount, inode.Number, 0);
            allocationGroup.ReleaseBlocks(oldAgBlock, oldExtent.BlockCount);
        }

        WriteAllocatedInode(inode, newFsBlock, checked((uint)newBlocks), checked((ulong)contentLength));
        allocationGroup.Commit();
        UpdateSuperBlock(freeBlockDelta: checked((long)oldBlocks - (long)newBlocks), freeInodeDelta: 0);
        FinishMutation();
    }

    public bool CanCreateFile(XfsNodeRef directory, string name, long contentLength, out string reason)
    {
        if (!ValidateForEditing(out reason))
        {
            return false;
        }

        if (!IsValidName(name) || Encoding.UTF8.GetByteCount(name) > 255 || contentLength < 0)
        {
            reason = "XFSファイル名またはサイズが不正です。";
            return false;
        }

        try
        {
            var parent = ReadInode(directory.Inode);
            ValidateLocalDirectory(parent);
            if (ReadDirectoryEntries(parent).Any(entry => entry.Name == name))
            {
                throw new IOException($"同名の項目が既に存在します: {name}");
            }

            ValidateShortFormCapacity(parent, name);
            var blocks = RequiredBlocks(checked((ulong)contentLength));
            if (blocks > 0x1fffff)
            {
                throw new NotSupportedException("実験版XFS編集では1 extent（最大2,097,151 blocks）のファイルだけを追加できます。");
            }

            var ag = new XfsAllocationGroup(this, GetInodeAllocationGroup(parent.Number), loadTrees: true);
            _ = ag.FindFreeInode();
            if (blocks > 0)
            {
                _ = ag.FindFreeExtent(checked((uint)blocks));
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public XfsNodeRef CreateFile(
        XfsNodeRef directory,
        string name,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("追加内容のストリームを読み取れません。", nameof(content));
        }

        if (!CanCreateFile(directory, name, contentLength, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        var parent = ReadInode(directory.Inode);
        var agNumber = GetInodeAllocationGroup(parent.Number);
        var ag = new XfsAllocationGroup(this, agNumber, loadTrees: true);
        var inodeNumber = ag.AllocateInode();
        var blocks = checked((uint)RequiredBlocks(checked((ulong)contentLength)));
        var agBlock = blocks == 0 ? 0U : ag.AllocateBlocks(blocks);
        var fsBlock = blocks == 0 ? 0UL : ToFileSystemBlock(agNumber, agBlock);
        WriteContiguousContent(fsBlock, blocks, content, contentLength, cancellationToken);
        if (blocks > 0)
        {
            ag.AddRmap(agBlock, blocks, inodeNumber, 0);
        }

        WriteNewInode(inodeNumber, parent, fsBlock, blocks, checked((ulong)contentLength));
        AddShortFormEntry(parent, name, inodeNumber);
        ag.Commit();
        UpdateSuperBlock(freeBlockDelta: -blocks, freeInodeDelta: -1);
        FinishMutation();
        return new XfsNodeRef(CombinePath(directory.Path, name), inodeNumber, XfsRawNodeKind.RegularFile);
    }

    public bool CanDeleteFile(XfsNodeRef directory, XfsNodeRef file, string name, out string reason)
    {
        if (!ValidateForEditing(out reason))
        {
            return false;
        }

        try
        {
            var parent = ReadInode(directory.Inode);
            var inode = ReadInode(file.Inode);
            ValidateLocalDirectory(parent);
            ValidateEditableRegularInode(inode);
            if (inode.LinkCount != 1 || !string.Equals(file.Path, CombinePath(directory.Path, name), StringComparison.Ordinal))
            {
                throw new NotSupportedException("hard linkを持たず、指定directory直下にある通常ファイルだけを削除できます。");
            }

            _ = FindShortFormEntry(parent, name, inode.Number);
            _ = GetSingleOwnedExtent(inode);
            var agNumber = GetInodeAllocationGroup(inode.Number);
            if (agNumber != GetInodeAllocationGroup(parent.Number))
            {
                throw new NotSupportedException("親directoryと異なるallocation groupのinodeはまだ削除できません。");
            }

            var ag = new XfsAllocationGroup(this, agNumber, loadTrees: true);
            ag.ValidateInodeCanBeFreed(inode.Number);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void DeleteFile(
        XfsNodeRef directory,
        XfsNodeRef file,
        string name,
        CancellationToken cancellationToken)
    {
        if (!CanDeleteFile(directory, file, name, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = ReadInode(directory.Inode);
        var inode = ReadInode(file.Inode);
        var oldExtent = GetSingleOwnedExtent(inode);
        var oldBlocks = RequiredBlocks(inode.Length);
        var agNumber = GetInodeAllocationGroup(inode.Number);
        var ag = new XfsAllocationGroup(this, agNumber, loadTrees: true);
        if (oldExtent is not null)
        {
            var agBlock = ToAllocationGroupBlock(agNumber, oldExtent.StartBlock);
            ag.RemoveRmap(agBlock, oldExtent.BlockCount, inode.Number, 0);
            ag.ReleaseBlocks(agBlock, oldExtent.BlockCount);
        }

        ag.FreeInode(inode.Number);
        RemoveShortFormEntry(parent, name, inode.Number);
        WriteFreedInode(inode);
        ag.Commit();
        UpdateSuperBlock(freeBlockDelta: checked((long)oldBlocks), freeInodeDelta: 1);
        FinishMutation();
    }

    public bool CanCreateDirectory(XfsNodeRef directory, string name, out string reason)
    {
        if (!ValidateForEditing(out reason) || !IsValidName(name) || Encoding.UTF8.GetByteCount(name) > 255)
        {
            reason = string.IsNullOrEmpty(reason) ? "XFSディレクトリ名が不正です。" : reason;
            return false;
        }

        try
        {
            var parent = ReadInode(directory.Inode);
            ValidateLocalDirectory(parent);
            if (ReadDirectoryEntries(parent).Any(entry => entry.Name == name))
            {
                throw new IOException($"同名の項目が既に存在します: {name}");
            }

            ValidateShortFormCapacity(parent, name);
            var ag = new XfsAllocationGroup(this, GetInodeAllocationGroup(parent.Number), loadTrees: true);
            _ = ag.FindFreeInode();
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public XfsNodeRef CreateDirectory(
        XfsNodeRef directory,
        string name,
        CancellationToken cancellationToken)
    {
        if (!CanCreateDirectory(directory, name, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = ReadInode(directory.Inode);
        var ag = new XfsAllocationGroup(this, GetInodeAllocationGroup(parent.Number), loadTrees: true);
        var inodeNumber = ag.AllocateInode();
        WriteNewDirectoryInode(inodeNumber, parent);
        AddShortFormEntry(parent, name, inodeNumber, fileType: 2);
        UpdateInodeLinkCount(parent, 1);
        ag.Commit();
        UpdateSuperBlock(freeBlockDelta: 0, freeInodeDelta: -1);
        FinishMutation();
        var created = ReadInode(inodeNumber);
        ValidateLocalDirectory(created);
        if (!ReadDirectoryEntries(ReadInode(parent.Number)).Any(entry => entry.Name == name && entry.Inode == inodeNumber))
        {
            throw new InvalidDataException("作成したXFSディレクトリエントリを再確認できません。");
        }

        return new XfsNodeRef(CombinePath(directory.Path, name), inodeNumber, XfsRawNodeKind.Directory);
    }

    public bool CanDeleteDirectory(
        XfsNodeRef parentDirectory,
        XfsNodeRef directory,
        string name,
        out string reason)
    {
        if (!ValidateForEditing(out reason))
        {
            return false;
        }

        try
        {
            var parent = ReadInode(parentDirectory.Inode);
            var inode = ReadInode(directory.Inode);
            ValidateLocalDirectory(parent);
            ValidateLocalDirectory(inode);
            if (inode.Number == _superBlock.RootInode
                || inode.LinkCount != 2
                || !string.Equals(directory.Path, CombinePath(parentDirectory.Path, name), StringComparison.Ordinal)
                || ReadDirectoryEntries(inode).Count != 0)
            {
                throw new NotSupportedException("指定した親の直下にある通常の空XFSディレクトリだけを削除できます。");
            }

            _ = FindShortFormEntry(parent, name, inode.Number);
            var agNumber = GetInodeAllocationGroup(inode.Number);
            if (agNumber != GetInodeAllocationGroup(parent.Number))
            {
                throw new NotSupportedException("親directoryと異なるallocation groupのinodeはまだ削除できません。");
            }

            var ag = new XfsAllocationGroup(this, agNumber, loadTrees: true);
            ag.ValidateInodeCanBeFreed(inode.Number);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void DeleteDirectory(
        XfsNodeRef parentDirectory,
        XfsNodeRef directory,
        string name,
        CancellationToken cancellationToken)
    {
        if (!CanDeleteDirectory(parentDirectory, directory, name, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = ReadInode(parentDirectory.Inode);
        var inode = ReadInode(directory.Inode);
        var ag = new XfsAllocationGroup(this, GetInodeAllocationGroup(inode.Number), loadTrees: true);
        ag.FreeInode(inode.Number);
        RemoveShortFormEntry(parent, name, inode.Number);
        UpdateInodeLinkCount(parent, -1);
        WriteFreedInode(inode);
        ag.Commit();
        UpdateSuperBlock(freeBlockDelta: 0, freeInodeDelta: 1);
        FinishMutation();
    }

    public bool CanMoveEntry(
        XfsNodeRef sourceDirectory,
        XfsNodeRef entry,
        XfsNodeRef destinationDirectory,
        string sourceName,
        string destinationName,
        out string reason)
    {
        if (!ValidateForEditing(out reason)
            || !IsValidName(destinationName)
            || Encoding.UTF8.GetByteCount(destinationName) > 255)
        {
            reason = string.IsNullOrEmpty(reason) ? "XFS移動先名が不正です。" : reason;
            return false;
        }

        if (!string.Equals(entry.Path, CombinePath(sourceDirectory.Path, sourceName), StringComparison.Ordinal))
        {
            reason = "移動対象が指定した移動元ディレクトリの直下にありません。";
            return false;
        }

        var destinationPath = CombinePath(destinationDirectory.Path, destinationName);
        if (string.Equals(entry.Path, destinationPath, StringComparison.Ordinal))
        {
            reason = "移動元と移動先が同じです。";
            return false;
        }

        if (entry.Kind == XfsRawNodeKind.Directory
            && (string.Equals(destinationDirectory.Path, entry.Path, StringComparison.Ordinal)
                || destinationDirectory.Path.StartsWith(entry.Path.TrimEnd('\\') + "\\", StringComparison.Ordinal)))
        {
            reason = "ディレクトリを自分自身の配下へ移動できません。";
            return false;
        }

        try
        {
            var sourceParent = ReadInode(sourceDirectory.Inode);
            var destinationParent = ReadInode(destinationDirectory.Inode);
            var inode = ReadInode(entry.Inode);
            ValidateLocalDirectory(sourceParent);
            ValidateLocalDirectory(destinationParent);
            if (entry.Kind == XfsRawNodeKind.Directory)
            {
                ValidateLocalDirectory(inode);
            }
            else
            {
                ValidateEditableRegularInode(inode);
            }

            if (ReadDirectoryEntries(destinationParent).Any(candidate => candidate.Name == destinationName))
            {
                throw new IOException($"移動先には同名の項目が既に存在します: {destinationName}");
            }

            _ = FindShortFormEntry(sourceParent, sourceName, inode.Number);
            ValidateShortFormCapacity(destinationParent, destinationName);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public XfsNodeRef MoveEntry(
        XfsNodeRef sourceDirectory,
        XfsNodeRef entry,
        XfsNodeRef destinationDirectory,
        string sourceName,
        string destinationName,
        CancellationToken cancellationToken)
    {
        if (!CanMoveEntry(
                sourceDirectory,
                entry,
                destinationDirectory,
                sourceName,
                destinationName,
                out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceParent = ReadInode(sourceDirectory.Inode);
        var destinationParent = ReadInode(destinationDirectory.Inode);
        var inode = ReadInode(entry.Inode);
        AddShortFormEntry(
            destinationParent,
            destinationName,
            inode.Number,
            inode.IsDirectory ? (byte)2 : (byte)1);
        RemoveShortFormEntry(sourceParent, sourceName, inode.Number);
        if (inode.IsDirectory && sourceParent.Number != destinationParent.Number)
        {
            UpdateShortFormParent(inode, destinationParent.Number);
            UpdateInodeLinkCount(sourceParent, -1);
            UpdateInodeLinkCount(destinationParent, 1);
        }

        WriteInodeChangeTime(inode);
        FinishMutation();
        return new XfsNodeRef(
            CombinePath(destinationDirectory.Path, destinationName),
            inode.Number,
            entry.Kind);
    }

    public bool CanSetAttributes(XfsNodeRef entry, FileAttributes attributes, out string reason)
    {
        if (!ValidateForEditing(out reason))
        {
            return false;
        }

        var allowed = FileAttributes.ReadOnly | FileAttributes.Directory;
        if ((attributes & ~allowed) != 0
            || attributes.HasFlag(FileAttributes.Directory) != (entry.Kind == XfsRawNodeKind.Directory))
        {
            reason = "XFSではReadOnly属性だけを編集でき、Directory属性は変更できません。";
            return false;
        }

        try
        {
            var inode = ReadInode(entry.Inode);
            if (entry.Kind == XfsRawNodeKind.Directory)
            {
                ValidateLocalDirectory(inode);
            }
            else
            {
                ValidateEditableRegularInode(inode);
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void SetAttributes(
        XfsNodeRef entry,
        FileAttributes attributes,
        CancellationToken cancellationToken)
    {
        if (!CanSetAttributes(entry, attributes, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var inode = ReadInode(entry.Inode);
        var mode = attributes.HasFlag(FileAttributes.ReadOnly)
            ? (ushort)(inode.Mode & ~0x92)
            : (ushort)(inode.Mode | 0x80);
        WriteInodeMode(inode, mode);
        FinishMutation();
    }

    public bool CanSetLastWriteTimeUtc(XfsNodeRef entry, DateTime modifiedUtc, out string reason)
    {
        if (!ValidateForEditing(out reason))
        {
            return false;
        }

        try
        {
            var seconds = new DateTimeOffset(modifiedUtc.ToUniversalTime()).ToUnixTimeSeconds();
            var inode = ReadInode(entry.Inode);
            if ((inode.Flags2 & BigTimeInodeFlag) == 0 && (seconds < int.MinValue || seconds > int.MaxValue))
            {
                reason = "このXFS inodeの更新日時は1901年から2038年の範囲で指定してください。";
                return false;
            }

            if (entry.Kind == XfsRawNodeKind.Directory)
            {
                ValidateLocalDirectory(inode);
            }
            else
            {
                ValidateEditableRegularInode(inode);
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or OverflowException or ArgumentOutOfRangeException)
        {
            reason = ex.Message;
            return false;
        }
    }

    public void SetLastWriteTimeUtc(
        XfsNodeRef entry,
        DateTime modifiedUtc,
        CancellationToken cancellationToken)
    {
        if (!CanSetLastWriteTimeUtc(entry, modifiedUtc, out var reason))
        {
            throw new NotSupportedException(reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteInodeModifiedTime(ReadInode(entry.Inode), modifiedUtc.ToUniversalTime());
        FinishMutation();
    }

    private void ValidateEditableRegularInode(XfsInode inode)
    {
        if (inode.FileType != 8 || inode.Version != 3 || inode.LinkCount != 1)
        {
            throw new NotSupportedException("v3のhard linkを持たない通常XFSファイルだけを編集できます。");
        }

        if ((inode.Flags & 0x0031) != 0 || (inode.Flags2 & ReflinkInodeFlag) != 0)
        {
            throw new NotSupportedException("realtime、immutable、append-only、reflinkのXFSファイルは編集できません。");
        }

        if (inode.Format != 2)
        {
            throw new NotSupportedException("inline extent形式のXFSファイルだけを編集できます。");
        }

        ValidateChecksum(inode.RawData, 0x64, $"XFS inode {inode.Number}");
        _ = ValidateFullyAllocated(inode);
    }

    private void ValidateAllocationChange(XfsInode inode, ulong newBlocks)
    {
        if (newBlocks > 0x1fffff)
        {
            throw new NotSupportedException("実験版XFS編集では1 extent（最大2,097,151 blocks）のサイズ変更だけに対応しています。");
        }

        _ = GetSingleOwnedExtent(inode);
        var agNumber = GetInodeAllocationGroup(inode.Number);
        var ag = new XfsAllocationGroup(this, agNumber, loadTrees: true);
        if (newBlocks > 0)
        {
            _ = ag.FindFreeExtent(checked((uint)newBlocks));
        }
    }

    private XfsExtent? GetSingleOwnedExtent(XfsInode inode)
    {
        var extents = GetExtents(inode).ToArray();
        var blocks = RequiredBlocks(inode.Length);
        if (blocks == 0 && extents.Length == 0)
        {
            return null;
        }

        if (extents.Length != 1
            || extents[0].StartOffset != 0
            || extents[0].BlockCount != blocks
            || extents[0].IsUnwritten)
        {
            throw new NotSupportedException("preallocation、sparse、複数extentを持つXFSファイルのallocation変更は未対応です。");
        }

        var ag = extents[0].StartBlock >> _superBlock.AgBlocksLog2;
        if (ag != GetInodeAllocationGroup(inode.Number))
        {
            throw new NotSupportedException("inodeと異なるallocation groupにあるXFS extentはまだ変更できません。");
        }

        return extents[0];
    }

    private void ValidateLocalDirectory(XfsInode directory)
    {
        if (!directory.IsDirectory || directory.Version != 3 || directory.Format != 1 || (ulong)directory.DataFork.Length < directory.Length)
        {
            throw new NotSupportedException("v3 short-form XFS directoryだけを編集できます。");
        }

        if ((directory.Flags & 0x0031) != 0 || directory.DataFork[1] != 0)
        {
            throw new NotSupportedException("保護属性または64-bit inode entryを持つXFS directoryは編集できません。");
        }

        ValidateChecksum(directory.RawData, 0x64, $"XFS directory inode {directory.Number}");
    }

    private void ValidateShortFormCapacity(XfsInode directory, string name)
    {
        var required = checked(3 + Encoding.UTF8.GetByteCount(name) + (_superBlock.HasFType ? 1 : 0) + 4);
        if (directory.Length + (ulong)required > (ulong)directory.DataFork.Length || directory.DataFork[0] == byte.MaxValue)
        {
            throw new NotSupportedException("short-form directory内に新しいentryを置く空きがありません。");
        }
    }

    private (int Offset, int Length) FindShortFormEntry(XfsInode directory, string name, ulong inodeNumber)
    {
        var data = directory.DataFork;
        var offset = 6;
        for (var index = 0; index < data[0]; index++)
        {
            if ((ulong)(offset + 3) > directory.Length)
            {
                break;
            }

            var nameLength = data[offset];
            var length = checked(3 + nameLength + (_superBlock.HasFType ? 1 : 0) + 4);
            var nameOffset = offset + 3;
            var inodeOffset = nameOffset + nameLength + (_superBlock.HasFType ? 1 : 0);
            if ((ulong)(offset + length) > directory.Length)
            {
                break;
            }

            if (Encoding.UTF8.GetString(data, nameOffset, nameLength) == name
                && EndianUtilities.ReadUInt32Big(data, inodeOffset) == inodeNumber)
            {
                return (offset, length);
            }

            offset += length;
        }

        throw new FileNotFoundException($"XFS short-form directory entryを再確認できません: {name}");
    }

    private void AddShortFormEntry(XfsInode directory, string name, ulong inodeNumber, byte fileType = 1)
    {
        ValidateShortFormCapacity(directory, name);
        var raw = (byte[])directory.RawData.Clone();
        var fork = raw.AsSpan(0xb0, directory.DataFork.Length);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var offset = checked((int)directory.Length);
        ushort dataOffset = 0x60;
        var scan = 6;
        for (var index = 0; index < fork[0]; index++)
        {
            var oldNameLength = fork[scan];
            var oldDataOffset = BinaryPrimitives.ReadUInt16BigEndian(fork.Slice(scan + 1, 2));
            dataOffset = checked((ushort)(oldDataOffset + Align(8 + 1 + oldNameLength + (_superBlock.HasFType ? 1 : 0) + 2, 8)));
            scan += 3 + oldNameLength + (_superBlock.HasFType ? 1 : 0) + 4;
        }

        fork[offset] = checked((byte)nameBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(fork.Slice(offset + 1, 2), dataOffset);
        nameBytes.CopyTo(fork.Slice(offset + 3));
        var cursor = offset + 3 + nameBytes.Length;
        if (_superBlock.HasFType)
        {
            fork[cursor++] = fileType;
        }

        BinaryPrimitives.WriteUInt32BigEndian(fork.Slice(cursor, 4), checked((uint)inodeNumber));
        fork[0]++;
        var newSize = checked(directory.Length + (ulong)(cursor + 4 - offset));
        WriteDirectoryInode(directory, raw, newSize);
    }

    private void RemoveShortFormEntry(XfsInode directory, string name, ulong inodeNumber)
    {
        var location = FindShortFormEntry(directory, name, inodeNumber);
        var raw = (byte[])directory.RawData.Clone();
        var fork = raw.AsSpan(0xb0, directory.DataFork.Length);
        var oldSize = checked((int)directory.Length);
        fork.Slice(location.Offset + location.Length, oldSize - location.Offset - location.Length)
            .CopyTo(fork.Slice(location.Offset));
        fork.Slice(oldSize - location.Length, location.Length).Clear();
        fork[0]--;
        WriteDirectoryInode(directory, raw, checked(directory.Length - (ulong)location.Length));
    }

    private void WriteDirectoryInode(XfsInode directory, byte[] raw, ulong size)
    {
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x38, 8), size);
        SetModificationTimes(raw);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(directory.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteInodeSize(XfsInode inode, ulong size)
    {
        var raw = (byte[])inode.RawData.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x38, 8), size);
        SetModificationTimes(raw);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteAllocatedInode(XfsInode inode, ulong startBlock, uint blocks, ulong size)
    {
        var raw = (byte[])inode.RawData.Clone();
        var dataLength = inode.ForkOffset == 0 ? raw.Length - 0xb0 : inode.ForkOffset * 8 - 0xb0;
        raw.AsSpan(0xb0, dataLength).Clear();
        if (blocks > 0)
        {
            WriteExtent(raw.AsSpan(0xb0, 16), startBlock, blocks);
        }

        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x38, 8), size);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x40, 8), blocks);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x4c, 4), blocks == 0 ? 0U : 1U);
        SetModificationTimes(raw);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteNewInode(ulong inodeNumber, XfsInode parent, ulong startBlock, uint blocks, ulong size)
    {
        var offset = GetInodeDiskOffset(inodeNumber);
        var old = ReadMetadata(offset, _superBlock.InodeSize);
        var raw = new byte[_superBlock.InodeSize];
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(0, 2), InodeMagic);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2, 2), 0x81a4);
        raw[4] = 3;
        raw[5] = 2;
        parent.RawData.AsSpan(8, 8).CopyTo(raw.AsSpan(8, 8));
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x10, 4), 1);
        parent.RawData.AsSpan(0x14, 4).CopyTo(raw.AsSpan(0x14, 4));
        BinaryPrimitives.WriteUInt64BigEndian(
            raw.AsSpan(0x78, 8),
            (_superBlock.IncompatibleFeatures & BigTimeFeature) != 0 ? BigTimeInodeFlag : 0);
        SetAllTimes(raw);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x38, 8), size);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x40, 8), blocks);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x4c, 4), blocks == 0 ? 0U : 1U);
        raw[0x53] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x5c, 4), checked((uint)Random.Shared.Next(1, int.MaxValue)));
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x60, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x68, 8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x98, 8), inodeNumber);
        _superBlock.Uuid.CopyTo(raw.AsSpan(0xa0, 16));
        if (blocks > 0)
        {
            WriteExtent(raw.AsSpan(0xb0, 16), startBlock, blocks);
        }

        // A free v3 inode must already identify this physical slot.
        if (old[4] != 3 || EndianUtilities.ReadUInt64Big(old, 0x98) != inodeNumber)
        {
            throw new InvalidDataException("XFS free inode slotのself-identificationが一致しません。");
        }

        ValidateChecksum(old, 0x64, $"XFS free inode {inodeNumber}");

        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(offset, raw, 0, raw.Length);
    }

    private void WriteNewDirectoryInode(ulong inodeNumber, XfsInode parent)
    {
        var offset = GetInodeDiskOffset(inodeNumber);
        var old = ReadMetadata(offset, _superBlock.InodeSize);
        var raw = new byte[_superBlock.InodeSize];
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(0, 2), InodeMagic);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2, 2), 0x41ed);
        raw[4] = 3;
        raw[5] = 1;
        parent.RawData.AsSpan(8, 8).CopyTo(raw.AsSpan(8, 8));
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x10, 4), 2);
        parent.RawData.AsSpan(0x14, 4).CopyTo(raw.AsSpan(0x14, 4));
        BinaryPrimitives.WriteUInt64BigEndian(
            raw.AsSpan(0x78, 8),
            (_superBlock.IncompatibleFeatures & BigTimeFeature) != 0 ? BigTimeInodeFlag : 0);
        SetAllTimes(raw);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x38, 8), 6);
        raw[0x53] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x5c, 4), checked((uint)Random.Shared.Next(1, int.MaxValue)));
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x60, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x68, 8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x98, 8), inodeNumber);
        _superBlock.Uuid.CopyTo(raw.AsSpan(0xa0, 16));
        raw[0xb0] = 0;
        raw[0xb1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0xb2, 4), checked((uint)parent.Number));

        if (old[4] != 3 || EndianUtilities.ReadUInt64Big(old, 0x98) != inodeNumber)
        {
            throw new InvalidDataException("XFS free inode slotのself-identificationが一致しません。");
        }

        ValidateChecksum(old, 0x64, $"XFS free inode {inodeNumber}");
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(offset, raw, 0, raw.Length);
    }

    private void WriteFreedInode(XfsInode inode)
    {
        var raw = (byte[])inode.RawData.Clone();
        raw.AsSpan(2, raw.Length - 2).Clear();
        raw[4] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x60, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x98, 8), inode.Number);
        _superBlock.Uuid.CopyTo(raw.AsSpan(0xa0, 16));
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void UpdateInodeLinkCount(XfsInode inode, int delta)
    {
        var raw = ReadMetadata(inode.DiskOffset, inode.RawData.Length);
        var linkCount = checked((long)EndianUtilities.ReadUInt32Big(raw, 0x10) + delta);
        if (linkCount is < 0 or > uint.MaxValue)
        {
            throw new InvalidDataException("XFS inode link countが範囲外になります。");
        }

        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x10, 4), checked((uint)linkCount));
        SetModificationTimes(raw);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void UpdateShortFormParent(XfsInode directory, ulong parentInodeNumber)
    {
        ValidateLocalDirectory(directory);
        var raw = (byte[])directory.RawData.Clone();
        if (raw[0xb1] != 0)
        {
            throw new NotSupportedException("64-bit parent inodeを持つXFS short-form directoryは移動できません。");
        }

        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0xb2, 4), checked((uint)parentInodeNumber));
        SetModificationTimes(raw);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(directory.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteInodeChangeTime(XfsInode inode)
    {
        var raw = ReadMetadata(inode.DiskOffset, inode.RawData.Length);
        SetTimestamp(raw, 0x30);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteInodeMode(XfsInode inode, ushort mode)
    {
        var raw = ReadMetadata(inode.DiskOffset, inode.RawData.Length);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2, 2), mode);
        SetTimestamp(raw, 0x30);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteInodeModifiedTime(XfsInode inode, DateTime modifiedUtc)
    {
        var raw = ReadMetadata(inode.DiskOffset, inode.RawData.Length);
        SetTimestamp(raw, 0x28, modifiedUtc);
        SetTimestamp(raw, 0x30);
        IncrementChangeCount(raw);
        UpdateChecksum(raw, 0x64);
        _writer!.WriteAt(inode.DiskOffset, raw, 0, raw.Length);
    }

    private void WriteToExtents(
        IReadOnlyList<XfsExtent> extents,
        Stream content,
        long length,
        CancellationToken cancellationToken)
    {
        long remaining = length;
        foreach (var extent in extents)
        {
            var allocated = checked((long)extent.BlockCount * _superBlock.BlockSize);
            var count = Math.Min(remaining, allocated);
            CopyToDevice(ExtentToDiskOffset(extent.StartBlock), content, count, cancellationToken);
            if (count < allocated)
            {
                ZeroDevice(ExtentToDiskOffset(extent.StartBlock) + count, allocated - count, cancellationToken);
            }

            remaining -= count;
        }

        if (remaining != 0 || content.ReadByte() != -1)
        {
            throw new InvalidDataException("XFS編集内容のサイズが指定値と一致しません。");
        }
    }

    private void WriteContiguousContent(
        ulong startBlock,
        uint blocks,
        Stream content,
        long length,
        CancellationToken cancellationToken)
    {
        if (blocks == 0)
        {
            if (length != 0 || content.ReadByte() != -1)
            {
                throw new InvalidDataException("XFS編集内容のサイズが指定値と一致しません。");
            }

            return;
        }

        var allocated = checked((long)blocks * _superBlock.BlockSize);
        var offset = ExtentToDiskOffset(startBlock);
        CopyToDevice(offset, content, length, cancellationToken);
        ZeroDevice(offset + length, allocated - length, cancellationToken);
        if (content.ReadByte() != -1)
        {
            throw new InvalidDataException("XFS編集内容のサイズが指定値と一致しません。");
        }
    }

    private void CopyToDevice(long offset, Stream content, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long written = 0;
        while (written < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, length - written));
            content.ReadExactly(buffer.AsSpan(0, count));
            _writer!.WriteAt(offset + written, buffer, 0, count);
            written += count;
        }
    }

    private void ZeroDevice(long offset, long length, CancellationToken cancellationToken)
    {
        var zeros = new byte[Math.Min(1024 * 1024, checked((int)_superBlock.BlockSize))];
        long written = 0;
        while (written < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(zeros.Length, length - written));
            _writer!.WriteAt(offset + written, zeros, 0, count);
            written += count;
        }
    }

    private void UpdateSuperBlock(long freeBlockDelta, long freeInodeDelta)
    {
        var super = ReadMetadata(0, _superBlock.SectorSize);
        ValidateChecksum(super, 0xe0, "XFS superblock");
        WriteBigEndianWithDelta(super, 0x90, freeBlockDelta, "free block");
        WriteBigEndianWithDelta(super, 0x88, freeInodeDelta, "free inode");
        UpdateChecksum(super, 0xe0);
        _writer!.WriteAt(0, super, 0, super.Length);
    }

    private static void WriteBigEndianWithDelta(byte[] data, int offset, long delta, string label)
    {
        var value = EndianUtilities.ReadUInt64Big(data, offset);
        ulong updated;
        if (delta >= 0)
        {
            updated = checked(value + (ulong)delta);
        }
        else
        {
            var amount = checked((ulong)-delta);
            if (amount > value)
            {
                throw new InvalidDataException($"XFS {label} countが負になります。");
            }

            updated = value - amount;
        }

        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), updated);
    }

    private void FinishMutation()
    {
        _writer!.Flush();
        _inodeCache.Clear();
        _extentCache.Clear();
        _directoryCache.Clear();
    }

    private ulong RequiredBlocks(ulong size) =>
        size == 0 ? 0 : checked((size + _superBlock.BlockSize - 1) / _superBlock.BlockSize);

    private uint GetInodeAllocationGroup(ulong inode) =>
        checked((uint)(inode >> (_superBlock.AgBlocksLog2 + _superBlock.InodesPerBlockLog2)));

    private uint GetAllocationGroupInode(ulong inode) => checked((uint)(inode & _superBlock.RelativeInodeMask));

    private ulong ToFileSystemBlock(uint ag, uint agBlock) =>
        ((ulong)ag << _superBlock.AgBlocksLog2) | agBlock;

    private uint ToAllocationGroupBlock(uint expectedAg, ulong fsBlock)
    {
        var actualAg = checked((uint)(fsBlock >> _superBlock.AgBlocksLog2));
        if (actualAg != expectedAg)
        {
            throw new NotSupportedException("複数allocation groupにまたがるXFS操作は未対応です。");
        }

        return checked((uint)(fsBlock & ((1UL << _superBlock.AgBlocksLog2) - 1)));
    }

    private long GetInodeDiskOffset(ulong number)
    {
        var ag = GetInodeAllocationGroup(number);
        var relative = GetAllocationGroupInode(number);
        var block = relative >> _superBlock.InodesPerBlockLog2;
        var slot = relative & ((1U << _superBlock.InodesPerBlockLog2) - 1);
        return checked(
            ((long)ag * _superBlock.AgBlocks + block) * _superBlock.BlockSize
            + (long)slot * _superBlock.InodeSize);
    }

    private byte[] ReadMetadata(long offset, int count) => EndianUtilities.ReadBytes(_reader, offset, count);

    private void SetAllTimes(byte[] inode)
    {
        SetTimestamp(inode, 0x20);
        SetTimestamp(inode, 0x28);
        SetTimestamp(inode, 0x30);
        SetTimestamp(inode, 0x90);
    }

    private void SetModificationTimes(byte[] inode)
    {
        SetTimestamp(inode, 0x28);
        SetTimestamp(inode, 0x30);
    }

    private static void SetTimestamp(byte[] inode, int offset)
    {
        SetTimestamp(inode, offset, DateTime.UtcNow);
    }

    private static void SetTimestamp(byte[] inode, int offset, DateTime utc)
    {
        var value = new DateTimeOffset(utc.ToUniversalTime());
        var seconds = value.ToUnixTimeSeconds();
        var nanoseconds = checked((uint)((value.Ticks % TimeSpan.TicksPerSecond) * 100));
        var bigTime = EndianUtilities.ReadUInt64Big(inode, 0x78) is var flags2
            && (flags2 & BigTimeInodeFlag) != 0;
        if (bigTime)
        {
            var encoded = checked(((ulong)(seconds + 2_147_483_648L) * 1_000_000_000UL) + nanoseconds);
            BinaryPrimitives.WriteUInt64BigEndian(inode.AsSpan(offset, 8), encoded);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(inode.AsSpan(offset, 4), checked((int)seconds));
            BinaryPrimitives.WriteUInt32BigEndian(inode.AsSpan(offset + 4, 4), nanoseconds);
        }
    }

    private static void IncrementChangeCount(byte[] inode)
    {
        var count = EndianUtilities.ReadUInt64Big(inode, 0x68);
        BinaryPrimitives.WriteUInt64BigEndian(inode.AsSpan(0x68, 8), unchecked(count + 1));
    }

    private static void WriteExtent(Span<byte> destination, ulong startBlock, uint blockCount)
    {
        if (blockCount == 0 || blockCount > 0x1fffff || startBlock >= (1UL << 52))
        {
            throw new ArgumentOutOfRangeException(nameof(blockCount));
        }

        var high = startBlock >> 43;
        var low = (startBlock << 21) | blockCount;
        BinaryPrimitives.WriteUInt64BigEndian(destination[..8], high);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..16], low);
    }

    private static void ValidateChecksum(byte[] data, int checksumOffset, string label)
    {
        var expected = EndianUtilities.ReadUInt32Little(data, checksumOffset);
        var copy = (byte[])data.Clone();
        copy.AsSpan(checksumOffset, 4).Clear();
        var actual = ~ComputeXfsCrc32C(uint.MaxValue, copy);
        if (expected != actual)
        {
            throw new InvalidDataException($"{label} CRCが一致しません: expected=0x{expected:X8}, actual=0x{actual:X8}");
        }
    }

    private static void UpdateChecksum(byte[] data, int checksumOffset)
    {
        data.AsSpan(checksumOffset, 4).Clear();
        var checksum = ~ComputeXfsCrc32C(uint.MaxValue, data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(checksumOffset, 4), checksum);
    }

    private static uint ComputeXfsCrc32C(uint seed, ReadOnlySpan<byte> data)
    {
        const uint polynomial = 0x82f63b78;
        var checksum = seed;
        foreach (var value in data)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                checksum = (checksum >> 1) ^ ((checksum & 1) == 0 ? 0 : polynomial);
            }
        }

        return checksum;
    }

    private sealed class XfsAllocationGroup
    {
        private readonly XfsRawFileSystem _fs;
        private readonly uint _number;
        private readonly long _baseOffset;
        private readonly byte[] _agf;
        private readonly byte[] _agi;
        private XfsLeafTree? _bno;
        private XfsLeafTree? _count;
        private XfsLeafTree? _rmap;
        private XfsLeafTree? _inobt;
        private XfsLeafTree? _finobt;
        private List<XfsFreeRecord>? _freeRecords;
        private List<XfsRmapRecord>? _rmapRecords;
        private List<XfsInodeRecord>? _inodeRecords;
        private List<XfsInodeRecord>? _freeInodeRecords;
        private long _freeBlockDelta;
        private long _freeInodeDelta;

        public XfsAllocationGroup(XfsRawFileSystem fs, uint number, bool loadTrees)
        {
            _fs = fs;
            _number = number;
            _baseOffset = checked((long)number * fs._superBlock.AgBlocks * fs._superBlock.BlockSize);
            _agf = fs.ReadMetadata(_baseOffset + fs._superBlock.SectorSize, fs._superBlock.SectorSize);
            _agi = fs.ReadMetadata(_baseOffset + 2L * fs._superBlock.SectorSize, fs._superBlock.SectorSize);
            ValidateHeader(_agf, 0x58414746, 0xd8, "AGF");
            ValidateHeader(_agi, 0x58414749, 0x138, "AGI");
            if (loadTrees)
            {
                LoadTrees();
            }
        }

        public uint FreeBlocks => EndianUtilities.ReadUInt32Big(_agf, 0x34);
        public uint FreeListBlocks => EndianUtilities.ReadUInt32Big(_agf, 0x30);
        public uint InodeCount => EndianUtilities.ReadUInt32Big(_agi, 0x10);
        public uint FreeInodes => EndianUtilities.ReadUInt32Big(_agi, 0x1c);

        public uint FindFreeExtent(uint blocks)
        {
            EnsureTrees();
            var record = _freeRecords!
                .Where(candidate => candidate.BlockCount >= blocks)
                .OrderByDescending(candidate => candidate.BlockCount)
                .ThenBy(candidate => candidate.StartBlock)
                .FirstOrDefault();
            return record is null
                ? throw new NotSupportedException($"XFS allocation group {_number}に{blocks:N0}個の連続空きblockがありません。")
                : record.StartBlock;
        }

        public uint AllocateBlocks(uint blocks)
        {
            var start = FindFreeExtent(blocks);
            var freeRecords = _freeRecords ?? throw new InvalidOperationException("XFS free-space treeが未読です。");
            var record = freeRecords.Single(candidate => candidate.StartBlock == start);
            freeRecords.Remove(record);
            if (record.BlockCount > blocks)
            {
                freeRecords.Add(new XfsFreeRecord(checked(start + blocks), record.BlockCount - blocks));
            }

            _freeBlockDelta -= blocks;
            return start;
        }

        public void ReleaseBlocks(uint start, uint blocks)
        {
            EnsureTrees();
            var freeRecords = _freeRecords ?? throw new InvalidOperationException("XFS free-space treeが未読です。");
            ulong end = checked((ulong)start + blocks);
            if (freeRecords.Any(record => start < (ulong)record.StartBlock + record.BlockCount && record.StartBlock < end))
            {
                throw new InvalidDataException("XFS free-space btreeと解放対象が重複しています。");
            }

            var mergedStart = start;
            var mergedLength = blocks;
            var before = freeRecords.SingleOrDefault(record => (ulong)record.StartBlock + record.BlockCount == start);
            if (before is not null)
            {
                mergedStart = before.StartBlock;
                mergedLength = checked(mergedLength + before.BlockCount);
                freeRecords.Remove(before);
            }

            var after = freeRecords.SingleOrDefault(record => (ulong)mergedStart + mergedLength == record.StartBlock);
            if (after is not null)
            {
                mergedLength = checked(mergedLength + after.BlockCount);
                freeRecords.Remove(after);
            }

            freeRecords.Add(new XfsFreeRecord(mergedStart, mergedLength));
            _freeBlockDelta += blocks;
        }

        public ulong FindFreeInode()
        {
            EnsureTrees();
            foreach (var record in _inodeRecords!)
            {
                if (record.HoleMask != 0 || record.Count != 64 || record.FreeCount < 2)
                {
                    continue;
                }

                for (var bit = 0; bit < 64; bit++)
                {
                    if ((record.FreeMask & (1UL << bit)) != 0)
                    {
                        return ToFullInode(checked(record.StartInode + (uint)bit));
                    }
                }
            }

            throw new NotSupportedException("既存のfull inode chunk内に安全に割り当てられるinodeがありません。");
        }

        public ulong AllocateInode()
        {
            var inode = FindFreeInode();
            SetInodeFree(inode, free: false);
            return inode;
        }

        public void ValidateInodeCanBeFreed(ulong inode) => FindInodePair(inode);

        public void FreeInode(ulong inode) => SetInodeFree(inode, free: true);

        public void AddRmap(uint start, uint blocks, ulong owner, ulong offset)
        {
            EnsureTrees();
            var rmapRecords = _rmapRecords ?? throw new InvalidOperationException("XFS rmap treeが未読です。");
            if (rmapRecords.Any(record => start < (ulong)record.StartBlock + record.BlockCount
                && record.StartBlock < (ulong)start + blocks))
            {
                throw new InvalidDataException("XFS rmapbtに新規allocationと重なるrecordがあります。");
            }

            rmapRecords.Add(new XfsRmapRecord(start, blocks, owner, offset));
        }

        public void RemoveRmap(uint start, uint blocks, ulong owner, ulong offset)
        {
            EnsureTrees();
            var rmapRecords = _rmapRecords ?? throw new InvalidOperationException("XFS rmap treeが未読です。");
            var record = rmapRecords.SingleOrDefault(candidate =>
                candidate.Owner == owner
                && start >= candidate.StartBlock
                && (ulong)start + blocks <= (ulong)candidate.StartBlock + candidate.BlockCount
                && offset == candidate.Offset + start - candidate.StartBlock)
                ?? throw new InvalidDataException("XFS rmapbtで対象extentの所有recordを確認できません。");
            rmapRecords.Remove(record);
            var prefix = start - record.StartBlock;
            if (prefix > 0)
            {
                rmapRecords.Add(new XfsRmapRecord(record.StartBlock, prefix, owner, record.Offset));
            }

            var suffixStart = checked(start + blocks);
            var recordEnd = checked(record.StartBlock + record.BlockCount);
            if (suffixStart < recordEnd)
            {
                rmapRecords.Add(new XfsRmapRecord(
                    suffixStart,
                    recordEnd - suffixStart,
                    owner,
                    checked(record.Offset + suffixStart - record.StartBlock)));
            }
        }

        public void Commit()
        {
            EnsureTrees();
            var freeRecords = _freeRecords ?? throw new InvalidOperationException("XFS free-space treeが未読です。");
            var rmapRecords = _rmapRecords ?? throw new InvalidOperationException("XFS rmap treeが未読です。");
            var inodeRecords = _inodeRecords ?? throw new InvalidOperationException("XFS inode treeが未読です。");
            var freeInodeRecords = _freeInodeRecords ?? throw new InvalidOperationException("XFS free inode treeが未読です。");
            var bnoRecords = freeRecords.OrderBy(record => record.StartBlock).ToArray();
            var countRecords = freeRecords.OrderBy(record => record.BlockCount).ThenBy(record => record.StartBlock).ToArray();
            _bno!.WriteFreeRecords(bnoRecords);
            _count!.WriteFreeRecords(countRecords);
            _rmap!.WriteRmapRecords(rmapRecords.OrderBy(record => record.StartBlock).ThenBy(record => record.Owner).ThenBy(record => record.Offset).ToArray());
            _inobt!.WriteInodeRecords(inodeRecords);
            _finobt!.WriteInodeRecords(freeInodeRecords);

            WriteUInt32WithDelta(_agf, 0x34, _freeBlockDelta, "AGF free block");
            BinaryPrimitives.WriteUInt32BigEndian(
                _agf.AsSpan(0x38, 4),
                countRecords.Length == 0 ? 0 : countRecords.Max(record => record.BlockCount));
            WriteUInt32WithDelta(_agi, 0x1c, _freeInodeDelta, "AGI free inode");
            UpdateChecksum(_agf, 0xd8);
            UpdateChecksum(_agi, 0x138);
            _fs._writer!.WriteAt(_baseOffset + _fs._superBlock.SectorSize, _agf, 0, _agf.Length);
            _fs._writer!.WriteAt(_baseOffset + 2L * _fs._superBlock.SectorSize, _agi, 0, _agi.Length);
        }

        private void LoadTrees()
        {
            if (EndianUtilities.ReadUInt32Big(_agf, 0x1c) != 1
                || EndianUtilities.ReadUInt32Big(_agf, 0x20) != 1
                || EndianUtilities.ReadUInt32Big(_agf, 0x24) != 1
                || EndianUtilities.ReadUInt32Big(_agi, 0x18) != 1
                || EndianUtilities.ReadUInt32Big(_agi, 0x14c) != 1)
            {
                throw new NotSupportedException("実験版XFS編集はrootがleafであるAG btreeだけに対応しています。");
            }

            _bno = new XfsLeafTree(this, EndianUtilities.ReadUInt32Big(_agf, 0x10), BnoBtreeMagicV5, 8);
            _count = new XfsLeafTree(this, EndianUtilities.ReadUInt32Big(_agf, 0x14), CountBtreeMagicV5, 8);
            _rmap = new XfsLeafTree(this, EndianUtilities.ReadUInt32Big(_agf, 0x18), RmapBtreeMagicV5, 24);
            _inobt = new XfsLeafTree(this, EndianUtilities.ReadUInt32Big(_agi, 0x14), InodeBtreeMagicV5, 16);
            _finobt = new XfsLeafTree(this, EndianUtilities.ReadUInt32Big(_agi, 0x148), FreeInodeBtreeMagicV5, 16);
            _freeRecords = _bno.ReadFreeRecords();
            var countRecords = _count.ReadFreeRecords();
            ValidateFreeRecords(_freeRecords, countRecords);
            if (!_freeRecords.OrderBy(x => x.StartBlock).SequenceEqual(countRecords.OrderBy(x => x.StartBlock)))
            {
                throw new InvalidDataException("XFS bnobtとcntbtのfree-space recordsが一致しません。");
            }

            if (_freeRecords.Sum(record => (long)record.BlockCount) != FreeBlocks)
            {
                throw new InvalidDataException("XFS AGF free block countとfree-space btreeが一致しません。");
            }

            _rmapRecords = _rmap.ReadRmapRecords();
            _inodeRecords = _inobt.ReadInodeRecords();
            _freeInodeRecords = _finobt.ReadInodeRecords();
            ValidateInodeRecords(_inodeRecords, _freeInodeRecords);
        }

        private void ValidateFreeRecords(
            IReadOnlyList<XfsFreeRecord> bnoRecords,
            IReadOnlyList<XfsFreeRecord> countRecords)
        {
            var agLength = EndianUtilities.ReadUInt32Big(_agf, 0x0c);
            uint previousEnd = 0;
            foreach (var record in bnoRecords)
            {
                var end = checked((ulong)record.StartBlock + record.BlockCount);
                if (record.BlockCount == 0 || end > agLength || record.StartBlock < previousEnd)
                {
                    throw new InvalidDataException("XFS bnobtのfree-space recordが不正または重複しています。");
                }

                previousEnd = checked((uint)end);
            }

            XfsFreeRecord? previous = null;
            foreach (var record in countRecords)
            {
                if (previous is not null
                    && (record.BlockCount < previous.BlockCount
                        || record.BlockCount == previous.BlockCount && record.StartBlock < previous.StartBlock))
                {
                    throw new InvalidDataException("XFS cntbtのfree-space record順序が不正です。");
                }

                previous = record;
            }
        }

        private void ValidateInodeRecords(
            IReadOnlyList<XfsInodeRecord> inodeRecords,
            IReadOnlyList<XfsInodeRecord> freeInodeRecords)
        {
            uint previousEnd = 0;
            ulong inodeCount = 0;
            ulong freeCount = 0;
            foreach (var record in inodeRecords)
            {
                var bitmapFreeCount = System.Numerics.BitOperations.PopCount(record.FreeMask);
                if (record.HoleMask != 0
                    || record.Count != 64
                    || record.StartInode % 64 != 0
                    || record.StartInode < previousEnd
                    || record.FreeCount != bitmapFreeCount)
                {
                    throw new NotSupportedException(
                        "実験版XFS編集は整合したfull inode chunkだけを持つallocation groupに対応しています。");
                }

                previousEnd = checked(record.StartInode + 64);
                inodeCount = checked(inodeCount + record.Count);
                freeCount = checked(freeCount + record.FreeCount);
            }

            var expectedFinobt = inodeRecords.Where(record => record.FreeCount > 0).ToArray();
            if (expectedFinobt.Length != freeInodeRecords.Count)
            {
                throw new InvalidDataException("XFS inobtとfinobtのrecord数が一致しません。");
            }

            for (var index = 0; index < expectedFinobt.Length; index++)
            {
                var expected = expectedFinobt[index];
                var actual = freeInodeRecords[index];
                if (expected.StartInode != actual.StartInode
                    || expected.HoleMask != actual.HoleMask
                    || expected.Count != actual.Count
                    || expected.FreeCount != actual.FreeCount
                    || expected.FreeMask != actual.FreeMask)
                {
                    throw new InvalidDataException("XFS inobtとfinobtのinode recordsが一致しません。");
                }
            }

            if (inodeCount != InodeCount || freeCount != FreeInodes)
            {
                throw new InvalidDataException("XFS AGI inode countとinode btreeが一致しません。");
            }
        }

        private void EnsureTrees()
        {
            if (_bno is null)
            {
                LoadTrees();
            }
        }

        private void SetInodeFree(ulong inode, bool free)
        {
            var (record, freeRecord, bit) = FindInodePair(inode);
            var current = (record.FreeMask & (1UL << bit)) != 0;
            if (current == free)
            {
                throw new InvalidDataException("XFS inode allocation状態が想定と異なります。");
            }

            record.FreeMask = free ? record.FreeMask | (1UL << bit) : record.FreeMask & ~(1UL << bit);
            record.FreeCount = checked((byte)(record.FreeCount + (free ? 1 : -1)));
            freeRecord.FreeMask = record.FreeMask;
            freeRecord.FreeCount = record.FreeCount;
            _freeInodeDelta += free ? 1 : -1;
        }

        private (XfsInodeRecord Record, XfsInodeRecord FreeRecord, int Bit) FindInodePair(ulong inode)
        {
            EnsureTrees();
            var agInode = _fs.GetAllocationGroupInode(inode);
            var record = _inodeRecords!.SingleOrDefault(candidate =>
                agInode >= candidate.StartInode && agInode < candidate.StartInode + 64)
                ?? throw new InvalidDataException("XFS inobtにinode chunk recordがありません。");
            var freeRecord = _freeInodeRecords!.SingleOrDefault(candidate => candidate.StartInode == record.StartInode)
                ?? throw new NotSupportedException("finobtにrecordがないfull inode chunkの変更は未対応です。");
            return (record, freeRecord, checked((int)(agInode - record.StartInode)));
        }

        private ulong ToFullInode(uint agInode) =>
            ((ulong)_number << (_fs._superBlock.AgBlocksLog2 + _fs._superBlock.InodesPerBlockLog2)) | agInode;

        private void ValidateHeader(byte[] data, uint magic, int crcOffset, string label)
        {
            if (EndianUtilities.ReadUInt32Big(data, 0) != magic
                || EndianUtilities.ReadUInt32Big(data, 4) != 1
                || EndianUtilities.ReadUInt32Big(data, 8) != _number
                || !data.AsSpan(label == "AGF" ? 0x40 : 0x128, 16).SequenceEqual(_fs._superBlock.Uuid))
            {
                throw new InvalidDataException($"XFS {label} headerが不正です。");
            }

            ValidateChecksum(data, crcOffset, $"XFS {label} {_number}");
        }

        private static void WriteUInt32WithDelta(byte[] data, int offset, long delta, string label)
        {
            var value = EndianUtilities.ReadUInt32Big(data, offset);
            var updated = checked((long)value + delta);
            if (updated < 0 || updated > uint.MaxValue)
            {
                throw new InvalidDataException($"XFS {label} countが範囲外です。");
            }

            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), (uint)updated);
        }

        private sealed class XfsLeafTree
        {
            private readonly XfsAllocationGroup _ag;
            private readonly uint _agBlock;
            private readonly uint _magic;
            private readonly int _recordSize;
            private readonly byte[] _data;

            public XfsLeafTree(XfsAllocationGroup ag, uint agBlock, uint magic, int recordSize)
            {
                _ag = ag;
                _agBlock = agBlock;
                _magic = magic;
                _recordSize = recordSize;
                var agLength = EndianUtilities.ReadUInt32Big(ag._agf, 0x0c);
                if (agBlock == 0 || agBlock >= agLength)
                {
                    throw new InvalidDataException("XFS AG btree root blockがallocation group範囲外です。");
                }

                var offset = checked(ag._baseOffset + (long)agBlock * ag._fs._superBlock.BlockSize);
                _data = ag._fs.ReadMetadata(offset, checked((int)ag._fs._superBlock.BlockSize));
                var expectedDiskBlock = checked((ulong)offset / 512);
                if (EndianUtilities.ReadUInt32Big(_data, 0) != magic
                    || EndianUtilities.ReadUInt16Big(_data, 4) != 0
                    || EndianUtilities.ReadUInt32Big(_data, 8) != uint.MaxValue
                    || EndianUtilities.ReadUInt32Big(_data, 0x0c) != uint.MaxValue
                    || EndianUtilities.ReadUInt64Big(_data, 0x10) != expectedDiskBlock
                    || EndianUtilities.ReadUInt32Big(_data, 0x30) != ag._number
                    || !_data.AsSpan(0x20, 16).SequenceEqual(ag._fs._superBlock.Uuid))
                {
                    throw new InvalidDataException("XFS AG btree leaf headerが不正です。");
                }

                ValidateChecksum(_data, 0x34, "XFS AG btree leaf");
            }

            public List<XfsFreeRecord> ReadFreeRecords()
            {
                EnsureRecordSize(8);
                var result = new List<XfsFreeRecord>();
                for (var index = 0; index < RecordCount; index++)
                {
                    var offset = ShortBtreeHeaderSizeV5 + index * 8;
                    result.Add(new XfsFreeRecord(
                        EndianUtilities.ReadUInt32Big(_data, offset),
                        EndianUtilities.ReadUInt32Big(_data, offset + 4)));
                }

                return result;
            }

            public List<XfsRmapRecord> ReadRmapRecords()
            {
                EnsureRecordSize(24);
                var result = new List<XfsRmapRecord>();
                for (var index = 0; index < RecordCount; index++)
                {
                    var offset = ShortBtreeHeaderSizeV5 + index * 24;
                    result.Add(new XfsRmapRecord(
                        EndianUtilities.ReadUInt32Big(_data, offset),
                        EndianUtilities.ReadUInt32Big(_data, offset + 4),
                        EndianUtilities.ReadUInt64Big(_data, offset + 8),
                        EndianUtilities.ReadUInt64Big(_data, offset + 16)));
                }

                return result;
            }

            public List<XfsInodeRecord> ReadInodeRecords()
            {
                EnsureRecordSize(16);
                var result = new List<XfsInodeRecord>();
                for (var index = 0; index < RecordCount; index++)
                {
                    var offset = ShortBtreeHeaderSizeV5 + index * 16;
                    result.Add(new XfsInodeRecord(
                        EndianUtilities.ReadUInt32Big(_data, offset),
                        EndianUtilities.ReadUInt16Big(_data, offset + 4),
                        _data[offset + 6],
                        _data[offset + 7],
                        EndianUtilities.ReadUInt64Big(_data, offset + 8)));
                }

                return result;
            }

            public void WriteFreeRecords(IReadOnlyList<XfsFreeRecord> records) =>
                WriteRecords(records.Count, (recordIndex, offset) =>
                {
                    var record = records[recordIndex];
                    BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(offset, 4), record.StartBlock);
                    BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(offset + 4, 4), record.BlockCount);
                });

            public void WriteRmapRecords(IReadOnlyList<XfsRmapRecord> records) =>
                WriteRecords(records.Count, (recordIndex, offset) =>
                {
                    var record = records[recordIndex];
                    BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(offset, 4), record.StartBlock);
                    BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(offset + 4, 4), record.BlockCount);
                    BinaryPrimitives.WriteUInt64BigEndian(_data.AsSpan(offset + 8, 8), record.Owner);
                    BinaryPrimitives.WriteUInt64BigEndian(_data.AsSpan(offset + 16, 8), record.Offset);
                });

            public void WriteInodeRecords(IReadOnlyList<XfsInodeRecord> records) =>
                WriteRecords(records.Count, (recordIndex, offset) =>
                {
                    var record = records[recordIndex];
                    BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(offset, 4), record.StartInode);
                    BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(offset + 4, 2), record.HoleMask);
                    _data[offset + 6] = record.Count;
                    _data[offset + 7] = record.FreeCount;
                    BinaryPrimitives.WriteUInt64BigEndian(_data.AsSpan(offset + 8, 8), record.FreeMask);
                });

            private int RecordCount => EndianUtilities.ReadUInt16Big(_data, 6);

            private void EnsureRecordSize(int expected)
            {
                if (_recordSize != expected || RecordCount > (_data.Length - ShortBtreeHeaderSizeV5) / expected)
                {
                    throw new InvalidDataException("XFS AG btree leaf record countが不正です。");
                }
            }

            private void WriteRecords(int count, Action<int, int> write)
            {
                if (count > (_data.Length - ShortBtreeHeaderSizeV5) / _recordSize)
                {
                    throw new NotSupportedException("XFS AG btree leafにrecordを追加する空きがありません。");
                }

                _data.AsSpan(ShortBtreeHeaderSizeV5).Clear();
                BinaryPrimitives.WriteUInt16BigEndian(_data.AsSpan(6, 2), checked((ushort)count));
                for (var index = 0; index < count; index++)
                {
                    write(index, ShortBtreeHeaderSizeV5 + index * _recordSize);
                }

                UpdateChecksum(_data, 0x34);
                var offset = checked(_ag._baseOffset + (long)_agBlock * _ag._fs._superBlock.BlockSize);
                _ag._fs._writer!.WriteAt(offset, _data, 0, _data.Length);
            }
        }
    }

    private sealed record XfsFreeRecord(uint StartBlock, uint BlockCount);
    private sealed record XfsRmapRecord(uint StartBlock, uint BlockCount, ulong Owner, ulong Offset);

    private sealed class XfsInodeRecord(
        uint startInode,
        ushort holeMask,
        byte count,
        byte freeCount,
        ulong freeMask)
    {
        public uint StartInode { get; } = startInode;
        public ushort HoleMask { get; } = holeMask;
        public byte Count { get; } = count;
        public byte FreeCount { get; set; } = freeCount;
        public ulong FreeMask { get; set; } = freeMask;
    }
}
