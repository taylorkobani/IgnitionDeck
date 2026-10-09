using System.ComponentModel;

namespace IgnitionDeck.Core;

/// <summary>Adds context without replacing the original exception type or stack trace.</summary>
public static class OperationDiagnostics
{
    private const string OperationKey = "IgnitionDeck.Operation";

    public static T Execute<T>(string operation, Func<T> action)
    {
        try { return action(); }
        catch (Exception ex)
        {
            Annotate(ex, operation);
            throw;
        }
    }

    public static void Execute(string operation, Action action) => Execute(operation, () => { action(); return true; });

    public static void Annotate(Exception exception, string operation)
    {
        // Preserve the deepest/most specific operation if multiple boundaries see the exception.
        if (!exception.Data.Contains(OperationKey)) exception.Data[OperationKey] = operation;
    }

    public static string Describe(Exception exception)
    {
        var operation = exception.Data[OperationKey] as string;
        var native = exception is Win32Exception win32 ? $", Win32 {win32.NativeErrorCode}" : string.Empty;
        var cause = $"{exception.GetType().Name} (HRESULT 0x{exception.HResult:X8}{native}): {exception.Message}";
        return string.IsNullOrEmpty(operation) ? cause : $"{operation}\n{cause}";
    }
}
