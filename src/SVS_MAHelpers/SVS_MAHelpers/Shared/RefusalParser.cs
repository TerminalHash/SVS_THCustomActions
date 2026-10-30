#nullable enable
using System;

namespace SVS_MAHelpers
{
    /// <summary>
    /// Parses vanilla n_x / no_x reply scenario names. Stateless on purpose:
    /// the owning action's AskState decides whether the reply is in scope.
    /// </summary>
    internal static class RefusalParser
    {
        internal static bool TryParseNoScenario(string asset, out int number, out string kind)
        {
            number = -1;
            kind = "refusal";
            try
            {
                string name = asset.Trim();
                int s = name.LastIndexOfAny(new[] { '/', '\\' });
                if (s >= 0) name = name.Substring(s + 1);
                name = name.Trim();
                string digits;
                if (name.StartsWith("no_", StringComparison.OrdinalIgnoreCase)) { digits = name.Substring(3); kind = "NO (answer 1)"; }
                else if (name.StartsWith("n_", StringComparison.OrdinalIgnoreCase)) { digits = name.Substring(2); kind = "難聴 (answer 2)"; }
                else return false;
                int dot = digits.IndexOf('.');
                if (dot >= 0) digits = digits.Substring(0, dot);
                if (digits.Length == 0) return false;
                foreach (char c in digits)
                    if (!char.IsDigit(c)) return false;
                return int.TryParse(digits, out number);
            }
            catch { number = -1; return false; }
        }
    }
}
