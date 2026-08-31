using Blockbuster.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Blockbuster.Infrastructure.Operations;

public interface IAdministratorPinResetService
{
    Task ResetAsync(string pin, CancellationToken cancellationToken = default);
}

public interface ISecretPinReader
{
    string ReadFourDigitPin(string prompt);
}

public sealed class ConsoleSecretPinReader : ISecretPinReader
{
    public string ReadFourDigitPin(string prompt)
    {
        if (Console.IsInputRedirected)
            throw new InvalidOperationException("Administrator PIN reset requires an interactive terminal.");

        Console.Write(prompt);
        var digits = new List<char>(4);
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter && digits.Count == 4)
                break;
            if (key.Key == ConsoleKey.Backspace && digits.Count > 0)
                digits.RemoveAt(digits.Count - 1);
            else if (char.IsAsciiDigit(key.KeyChar) && digits.Count < 4)
                digits.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string([.. digits]);
    }
}

public sealed class OperatorCommandDispatcher(
    IDatabaseBackupService backups,
    IServiceProvider services,
    ISecretPinReader pinReader)
{
    public async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0)
        {
            WriteUsage();
            return 2;
        }

        if (string.Equals(arguments[0], "backup", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = ParseOutputPath(arguments);
            var backupPath = await backups.CreateBackupAsync(outputPath, cancellationToken);
            Console.WriteLine($"Backup created: {backupPath}");
            return 0;
        }

        if (arguments.Count == 2
            && string.Equals(arguments[0], "admin-pin", StringComparison.OrdinalIgnoreCase)
            && string.Equals(arguments[1], "reset", StringComparison.OrdinalIgnoreCase))
        {
            var resetter = services.GetService<IAdministratorPinResetService>();
            if (resetter is null)
            {
                Console.Error.WriteLine("Administrator credentials are not initialized yet; complete milestone 07 first.");
                return 2;
            }

            var pin = pinReader.ReadFourDigitPin("New four-digit administrator PIN: ");
            var confirmation = pinReader.ReadFourDigitPin("Confirm PIN: ");
            if (!string.Equals(pin, confirmation, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("PINs did not match.");
                return 2;
            }
            await resetter.ResetAsync(pin, cancellationToken);
            Console.WriteLine("Administrator PIN reset.");
            return 0;
        }

        WriteUsage();
        return 2;
    }

    private static string? ParseOutputPath(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 1)
            return null;
        if (arguments.Count == 3 && string.Equals(arguments[1], "--output", StringComparison.OrdinalIgnoreCase))
            return arguments[2];
        throw new ArgumentException("Usage: Blockbuster operator backup [--output <absolute-path>]");
    }

    private static void WriteUsage() => Console.Error.WriteLine(
        "Usage: Blockbuster operator backup [--output <absolute-path>] | operator admin-pin reset");
}
