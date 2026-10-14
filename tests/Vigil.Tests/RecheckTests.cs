using Vigil.Core;
using Xunit;

namespace Vigil.Tests;

/// <summary>
/// While the view is on it follows the player, and it also picks up pieces built or torn down
/// nearby, so it is checked again after the player moves a grid step and every few seconds.
/// </summary>
public sealed class RecheckTests
{
    private const float Spacing = 2f, Every = 3f;

    [Fact]
    public void TheFirstLookIsCheckedAtOnce()
    {
        Assert.True(Recheck.Due(checking: false, everChecked: false, now: 0f, lastStart: 0f, moved: 0f, Spacing, Every));
    }

    [Fact]
    public void AfterYouMoveAGridStepTheViewIsCheckedAgain()
    {
        Assert.True(Recheck.Due(checking: false, everChecked: true, now: 10.5f, lastStart: 10f, moved: 2.1f, Spacing, Every));
        Assert.True(Recheck.Due(checking: false, everChecked: true, now: 10.5f, lastStart: 10f, moved: Spacing, Spacing, Every));
        Assert.False(Recheck.Due(checking: false, everChecked: true, now: 10.5f, lastStart: 10f, moved: 1.5f, Spacing, Every));
    }

    [Fact]
    public void StandingStillTheViewIsStillCheckedEveryFewSecondsSoNewPiecesShow()
    {
        Assert.False(Recheck.Due(checking: false, everChecked: true, now: 12.9f, lastStart: 10f, moved: 0f, Spacing, Every));
        Assert.True(Recheck.Due(checking: false, everChecked: true, now: 13f, lastStart: 10f, moved: 0f, Spacing, Every));
    }

    [Fact]
    public void ACheckUnderwayIsLetFinishSoTheViewKeepsUpWhileYouWalk()
    {
        Assert.False(Recheck.Due(checking: true, everChecked: true, now: 50f, lastStart: 10f, moved: 30f, Spacing, Every));
        Assert.False(Recheck.Due(checking: true, everChecked: false, now: 0f, lastStart: 0f, moved: 0f, Spacing, Every));
    }
}
