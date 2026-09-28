using Qcow2Explorer.Core;

internal static class DiagnosticLogRobustnessTests
{
    public static void Run()
    {
        var successfulCalls = 0;
        EventHandler<string> throwingSubscriber = (_, _) =>
            throw new InvalidOperationException("subscriber failure");
        EventHandler<string> successfulSubscriber = (_, message) =>
        {
            if (message.Contains("subscriber-isolation", StringComparison.Ordinal))
            {
                successfulCalls++;
            }
        };

        DiagnosticLog.EntryAdded += throwingSubscriber;
        DiagnosticLog.EntryAdded += successfulSubscriber;
        try
        {
            DiagnosticLog.Write("subscriber-isolation");
        }
        finally
        {
            DiagnosticLog.EntryAdded -= throwingSubscriber;
            DiagnosticLog.EntryAdded -= successfulSubscriber;
        }

        if (successfulCalls != 1)
        {
            throw new InvalidOperationException(
                "Assertion failed: a failing diagnostic subscriber must not block later subscribers");
        }
    }
}
