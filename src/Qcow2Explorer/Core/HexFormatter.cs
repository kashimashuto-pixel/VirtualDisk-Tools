using System.Text;

namespace Qcow2Explorer.Core;

public static class HexFormatter
{
    public static string Format(byte[] data, long baseOffset = 0)
    {
        var builder = new StringBuilder();

        for (var row = 0; row < data.Length; row += 16)
        {
            var rowCount = Math.Min(16, data.Length - row);
            builder.Append((baseOffset + row).ToString("X8"));
            builder.Append("  ");

            for (var i = 0; i < 16; i++)
            {
                if (i < rowCount)
                {
                    builder.Append(data[row + i].ToString("X2"));
                }
                else
                {
                    builder.Append("  ");
                }

                builder.Append(i == 7 ? "  " : " ");
            }

            builder.Append(" ");
            for (var i = 0; i < rowCount; i++)
            {
                var b = data[row + i];
                builder.Append(b >= 0x20 && b <= 0x7e ? (char)b : '.');
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }
}
