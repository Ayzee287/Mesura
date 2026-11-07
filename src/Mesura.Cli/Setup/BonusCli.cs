using Mesura.Core.Bonus;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows;
using Mesura.Setup.Windows.Bonus;
using Mesura.Setup.Windows.Windows;

namespace Mesura.Cli.Setup;

/// <summary>
/// <c>mesura bonus cursors</c> (0.7): the bonus cursor pack, outside the profiles. <c>status</c> only reads; <c>apply</c> and <c>restore</c> show what they would do and change
/// the signed-in user's cursors only with <c>--apply</c> (the files go to Mesura's own folder, the roles are read back, the cursors from before are saved, and <c>restore</c> puts them back).
/// </summary>
internal static class BonusCli
{
    public const string Usage = """
        mesura bonus cursors [status]
          mesura bonus cursors apply [--apply]
          mesura bonus cursors restore [--apply]
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("cursors", StringComparison.OrdinalIgnoreCase))
        {
            return Bad("bonus: the only bonus is 'cursors'.");
        }

        var rest = args[1..];
        var verb = rest.Length == 0 || rest[0].StartsWith("--", StringComparison.Ordinal) ? "status" : rest[0].ToLowerInvariant();
        var apply = rest.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var tools = new SystemTools();
        var reader = new SystemStateReader(tools);
        var bonus = new CursorBonus(reader, new WindowsWriter(reader.User, tools), new CursorFiles(new SetupStore()), new CursorSession(ProcessUser.Is(reader.User)));
        switch (verb)
        {
            case "status":
                Console.WriteLine($"{CursorPack.SchemeName}: {bonus.State()}" + (bonus.CanRestore ? " (the cursors from before are saved)" : ""));
                if (bonus.BlockedReason is { } blocked)
                {
                    Console.WriteLine($"  {blocked}");
                }

                return 0;
            case "apply" or "restore":
                break;
            default:
                return Bad($"bonus cursors: unknown verb '{verb}'.");
        }

        if (bonus.BlockedReason is { } why)
        {
            Console.Error.WriteLine(why);
            return 1;
        }

        var state = bonus.State();
        var would = verb == "apply"
            ? state == CursorBonusState.Applied ? "nothing: the cursors are already applied." : $"copy {CursorPack.Files.Count} files to {new CursorFiles(new SetupStore()).Folder} and point {CursorPack.Roles.Count} cursor roles at them."
            : bonus.CanRestore ? "put back the cursors from before and remove the pack's files." : "nothing: the bonus has no saved cursors to put back.";
        Console.WriteLine($"{(apply ? "Doing" : "Would do")}: {would}");
        if (verb == "restore" && !bonus.CanRestore)
        {
            return 0; // nothing saved, nothing to do: not an error
        }

        if (!apply)
        {
            Console.WriteLine("Preview only; add --apply to do it.");
            return 0;
        }

        try
        {
            var result = verb == "apply" ? bonus.Apply() : bonus.Restore();
            Console.WriteLine(result.Detail + (result.Changed && !result.Reloaded ? " Windows will show them at the next sign-in." : ""));
            Console.WriteLine($"State now: {bonus.State()}");
            return 0;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Bad(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine($"Usage: {Usage}");
        return 2;
    }
}
