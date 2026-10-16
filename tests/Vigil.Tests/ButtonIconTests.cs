using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Every button in the panel carries an icon for what it does, picked by its first word, so no
    /// button stands bare beside the others. The buttons are read from the panel's code, so a new
    /// button with a word that has no icon fails here before it reaches the game.
    /// </summary>
    public sealed class ButtonIconTests
    {
        [Theory]
        [InlineData("Set", "check")]
        [InlineData("Compare", "compare")]
        [InlineData("Compare with the server", "compare")]
        [InlineData("Send to the server", "upload")]
        [InlineData("Reset", "reset")]
        [InlineData("Remove these 12 objects", "trash")]
        [InlineData("Press again to remove 12 objects for good", "trash")]
        [InlineData("Read names again", "refresh")]
        [InlineData("Running", "wait")]
        [InlineData("Open the recordings folder", "folder")]
        [InlineData("Run", "play")]
        [InlineData("Freeze", "pause")]
        [InlineData("Measure", "wait")]
        [InlineData("Back", "reset")]
        public void A_buttons_first_word_picks_its_icon(string label, string icon)
        {
            Assert.Equal(icon, ButtonIcon.For(label));
        }

        [Fact]
        public void Words_with_no_icon_get_none()
        {
            Assert.Null(ButtonIcon.For("Frobnicate"));
            Assert.Null(ButtonIcon.For(""));
            Assert.Null(ButtonIcon.For(null));
        }

        [Fact]
        public void Every_button_in_the_panel_has_an_icon()
        {
            var labels = PanelButtonLabels().ToList();
            Assert.True(labels.Count > 30, "only " + labels.Count + " button labels found; the scan has gone wrong");
            var bare = labels.Where(l => ButtonIcon.For(l.Label) == null).Select(l => "\"" + l.Label + "\" in " + l.Where).ToList();
            Assert.True(bare.Count == 0, "buttons without an icon: " + string.Join(", ", bare));
        }

        [Fact]
        public void Every_icon_a_button_can_get_is_drawn()
        {
            string skin = File.ReadAllText(Path.Combine(Root(), "src", "Host", "Skin.cs"));
            var drawn = Regex.Matches(skin, "Icons\\[\"([a-z-]+)\"\\] =").Cast<Match>().Select(m => m.Groups[1].Value).ToHashSet();
            var missing = ButtonIcon.Names.Where(n => !drawn.Contains(n)).ToList();
            Assert.True(missing.Count == 0, "icons named but not drawn: " + string.Join(", ", missing));
        }

        // ------------------------------------------------------------------ reading the buttons from the code

        private static readonly Dictionary<string, int> ActionArgument = new()
        {
            ["Tool"] = 2, ["PickTool"] = 2, ["ExpandTool"] = 5, ["Button"] = 0, ["Primary"] = 0, ["Danger"] = 0, ["Quiet"] = 0,
        };

        /// <summary>The words of every button the panel's code draws: each text in the argument that names the button.</summary>
        private static IEnumerable<(string Label, string Where)> PanelButtonLabels()
        {
            string src = Path.Combine(Root(), "src");
            var call = new Regex(@"\bUi\.(Tool|PickTool|ExpandTool|Button|Primary|Danger|Quiet)\(");
            foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file).StartsWith("Ui", StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(file)) == "Host") continue;
                string code = File.ReadAllText(file);
                foreach (Match m in call.Matches(code))
                {
                    var args = Arguments(code, m.Index + m.Length);
                    int index = ActionArgument[m.Groups[1].Value];
                    if (index >= args.Count) continue;
                    foreach (var text in Literals(args[index])) yield return (text, Path.GetFileName(file));
                }
            }
        }

        /// <summary>A call's arguments, split at its own commas, from just after its opening bracket.</summary>
        private static List<string> Arguments(string code, int at)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            for (int i = at; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"')
                {
                    int end = i + 1;
                    while (end < code.Length && code[end] != '"') end += code[end] == '\\' ? 2 : 1;
                    current.Append(code, i, end - i + 1);
                    i = end;
                    continue;
                }

                if (c == '(' || c == '[' || c == '{') depth++;
                if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0)
                    {
                        args.Add(current.ToString());
                        return args;
                    }

                    depth--;
                }

                if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            return args;
        }

        /// <summary>A button's words: the first text of each branch of the argument ("a ? \"Hide\" : \"Show\"" gives both; "\"Remove these \" + what" its start).</summary>
        private static IEnumerable<string> Literals(string argument)
        {
            foreach (var branch in Branches(argument))
            {
                var first = Regex.Match(branch, "\"((?:\\\\.|[^\"\\\\])*)\"");
                if (first.Success && first.Groups[1].Value.Trim().Length > 0) yield return first.Groups[1].Value;
            }
        }

        // The argument split at its own ? and :, outside texts and brackets.
        private static IEnumerable<string> Branches(string argument)
        {
            var current = new StringBuilder();
            int depth = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == '"')
                {
                    int end = i + 1;
                    while (end < argument.Length && argument[end] != '"') end += argument[end] == '\\' ? 2 : 1;
                    current.Append(argument, i, Math.Min(end, argument.Length - 1) - i + 1);
                    i = end;
                    continue;
                }

                if (c == '(' || c == '[' || c == '{') depth++;
                if (c == ')' || c == ']' || c == '}') depth--;
                if ((c == '?' || c == ':') && depth == 0)
                {
                    yield return current.ToString();
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            yield return current.ToString();
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Vigil.csproj")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("Vigil.csproj not found above the test folder");
        }
    }
}
