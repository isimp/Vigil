using System.Linq;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// What is loaded around the player, per object type: how many there are and what they ask
    /// of the renderer (meshes, shadow casters, lights, particle systems, sounds). Low frame rates
    /// at a large base are mostly drawing; this names the object types behind it.
    /// </summary>
    public sealed class CensusTests
    {
        [Fact]
        public void Instances_of_one_type_add_up()
        {
            var c = new Census();
            c.Add("piece_chair_bal", renderers: 3, shadowCasters: 2, lights: 0, particles: 0, audio: 0);
            c.Add("piece_chair_bal", renderers: 3, shadowCasters: 2, lights: 0, particles: 0, audio: 0);
            string[] lines = c.Format().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            string row = lines.Single(l => l.StartsWith("piece_chair_bal\t"));
            Assert.Equal("piece_chair_bal\t2\t6\t4\t0\t0\t0\t0\t", row);
        }

        [Fact]
        public void The_type_asking_most_of_the_renderer_comes_first_after_the_totals()
        {
            var c = new Census();
            c.Add("small", 1, 1, 0, 0, 0);
            c.Add("fire", 2, 0, 1, 3, 1);
            c.Add("big", 40, 40, 0, 0, 0);
            string[] lines = c.Format().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            Assert.Equal(Census.Header, lines[0]);
            Assert.StartsWith("(all)\t3\t43\t41\t1\t3\t1", lines[1]);
            Assert.StartsWith("big\t", lines[2]);
            Assert.StartsWith("fire\t", lines[3]);
            Assert.StartsWith("small\t", lines[4]);
        }

        [Fact]
        public void An_empty_census_still_has_its_header_and_totals()
        {
            string[] lines = new Census().Format().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("(all)\t0\t", lines[1]);
        }
    }
}
