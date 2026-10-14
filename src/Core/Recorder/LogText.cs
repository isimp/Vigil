using System.Text;

namespace Vigil.Core
{
    /// <summary>Turns a log message into a key that groups repeats of the same message.</summary>
    public static class LogText
    {
        private const int MaxLength = 160;

        /// <summary>First line only, every run of digits (with a leading minus) as '#', at most 160 characters.</summary>
        public static string Normalize(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "";
            int end = message.IndexOfAny(new[] { '\r', '\n' });
            string line = (end >= 0 ? message.Substring(0, end) : message).Trim();
            var sb = new StringBuilder(System.Math.Min(line.Length, MaxLength));
            for (int i = 0; i < line.Length && sb.Length < MaxLength; i++)
            {
                char c = line[i];
                bool minus = c == '-' && i + 1 < line.Length && char.IsDigit(line[i + 1]);
                if (char.IsDigit(c) || minus)
                {
                    sb.Append('#');
                    i++;
                    while (i < line.Length && char.IsDigit(line[i])) i++;
                    i--;
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
