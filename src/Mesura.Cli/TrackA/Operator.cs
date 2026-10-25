using Mesura.Core.Privacy;
using Mesura.Core.TrackA;

namespace Mesura.Cli.TrackA;

/// <summary>The operator's console: prompts and messages on stderr, answers from stdin (typed, or piped for a scripted run).</summary>
internal sealed class Operator(UserProfileRedactor profile)
{
    public void Say(string line = "") => Console.Error.WriteLine(profile.Redact(line));

    /// <summary>A line the operator must use as printed (a path, a command to paste): not rewritten. Only on this PC's own console.</summary>
    public static void SayExact(string line) => Console.Error.WriteLine(line);

    /// <summary>Reads one answer. A closed input stream ends the session: nothing half-answered is ever written.</summary>
    /// <exception cref="OperatorInputEndedException">The input ended.</exception>
    public string Ask(string prompt)
    {
        Console.Error.Write(profile.Redact(prompt));
        var line = Console.In.ReadLine() ?? throw new OperatorInputEndedException();
        return line.TrimStart('\uFEFF'); // a byte-order mark some shells put before piped answers
    }

    /// <summary>A yes/no question; Enter takes the default.</summary>
    public bool Confirm(string question, bool defaultYes = false)
    {
        while (true)
        {
            var answer = Ask($"{question} {(defaultYes ? "[Y/n]" : "[y/N]")} ").Trim();
            if (answer.Length == 0)
            {
                return defaultYes;
            }

            if (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase) || answer.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Say("  Type y or n.");
        }
    }

    /// <summary>
    /// Asks one observation field until the answer fits the field and passes the privacy screen. The screen's hit is
    /// named by category only; the answer is never repeated.
    /// </summary>
    /// <returns>The observation, or <c>null</c> for an optional field left empty.</returns>
    public Observation? AskField(ObservationField field, IReadOnlyList<LocalIdentifier> identifiers)
    {
        Say();
        Say(field.Question);
        Say($"  {field.Help}");
        if (field.Choices.Count > 0)
        {
            Say($"  {string.Join("  ", field.Choices.Select((choice, index) => $"{index + 1} {choice}"))}");
        }

        var escape = field.Optional ? "Enter = no note" : "Enter = unknown";
        if (field.AllowsNotApplicable)
        {
            escape += ", n/a = doesn't apply";
        }

        while (true)
        {
            var (observation, error) = field.Read(Ask($"  [{escape}] > "));
            if (error is not null)
            {
                Say($"  {error}");
                continue;
            }

            if (observation is null)
            {
                return null;
            }

            var hits = CapturePipeline.Screen(observation, identifiers);
            if (hits.Count > 0)
            {
                Say($"  That answer contains {string.Join(", ", hits)}. Answer again without it (it won't be kept).");
                continue;
            }

            return observation;
        }
    }
}

/// <summary>The operator's input stream closed before the questions were answered.</summary>
internal sealed class OperatorInputEndedException : Exception
{
    public OperatorInputEndedException()
        : base("The input ended before every question was answered.")
    {
    }
}
