using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Vigil.Tests;

/// <summary>
/// Comments and notes describe the code as it is and why it works so. They carry no dates or
/// clock times, which tell when something changed rather than what it is, and use no term of the
/// repository's local list, kept where git never commits it (<c>.git/info/forbidden-terms</c>, a
/// term a line, <c>#</c> for a note). Where the list is not there, as on a build server, that
/// check has nothing to hold.
/// </summary>
public sealed class CommentStandardTests
{
    private static readonly Regex DateOrTime = new(@"\b20\d\d-\d\d-\d\d\b|\b\d\d:\d\d\b", RegexOptions.CultureInvariant);

    [Fact]
    public void CommentsAndNotesCarryNoDatesOrClockTimes()
    {
        var found = Texts().Where(t => DateOrTime.IsMatch(t.Text)).Select(t => t.Where).ToList();

        Assert.True(found.Count == 0, "dates or clock times in: " + string.Join(", ", found));
    }

    [Fact]
    public void CommentsAndNotesUseNoTermOfTheLocalList()
    {
        var list = Path.Combine(Root(), ".git", "info", "forbidden-terms");
        if (!File.Exists(list))
        {
            return;
        }

        var terms = File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var found = Texts()
            .Where(t => terms.Any(term => Regex.IsMatch(t.Text, @"(?<!\w)" + Regex.Escape(term) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            .Select(t => t.Where)
            .ToList();

        Assert.True(found.Count == 0, "a term of the local list in: " + string.Join(", ", found));
    }

    [Theory]
    [InlineData("// as the run at 19:03 told", true)]
    [InlineData("/// the rules before 2026-10-05", true)]
    [InlineData("var url = \"https://example.org\"; // version 1.0.17, a 4:3 screen", false)]
    [InlineData("int a = 2; int b = 3;", false)]
    public void ACommentIsFoundAndChecked(string line, bool flagged)
    {
        string comment = CommentOf(line);
        Assert.Equal(flagged, comment != null && DateOrTime.IsMatch(comment));
    }

    /// <summary>The comment part of a source line: a line that is a comment, or the part after " // ".</summary>
    private static string CommentOf(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal) || trimmed.StartsWith('*'))
        {
            return trimmed;
        }

        int at = line.IndexOf(" // ", StringComparison.Ordinal);
        return at >= 0 ? line.Substring(at) : null;
    }

    /// <summary>Every comment in the sources and tests, and every line of the README, the changelog and the docs.</summary>
    private static IEnumerable<(string Where, string Text)> Texts()
    {
        string root = Root();
        var sources = new[] { "src", "tests" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.EndsWith(nameof(CommentStandardTests) + ".cs", StringComparison.Ordinal));
        foreach (var file in sources)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string comment = CommentOf(lines[i]);
                if (comment != null)
                {
                    yield return ($"{Path.GetRelativePath(root, file)}:{i + 1}", comment);
                }
            }
        }

        var notes = new[] { "README.md", "CHANGELOG.md" }.Select(n => Path.Combine(root, n)).Where(File.Exists)
            .Concat(Directory.Exists(Path.Combine(root, "docs")) ? Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories) : Enumerable.Empty<string>());
        foreach (var file in notes)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                yield return ($"{Path.GetRelativePath(root, file)}:{i + 1}", lines[i]);
            }
        }
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
