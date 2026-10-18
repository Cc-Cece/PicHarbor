using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class UpdateCloseCoordinatorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NoScheduledReadyUpdateNeverInterceptsClose(bool ready, bool scheduled)
    {
        var coordinator = new UpdateCloseCoordinator();
        coordinator.RequestClose(ready, scheduled).ShouldBe(UpdateCloseAction.Close);
        coordinator.IsHandlingClose.ShouldBeFalse();
    }

    [Fact]
    public void SecondCloseEscapesPendingInstallationInsteadOfRepeatingTheWait()
    {
        var coordinator = new UpdateCloseCoordinator();
        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.InstallOnExit);
        coordinator.IsHandlingClose.ShouldBeTrue();

        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.CancelInstallationAndClose);
        // The original asynchronous handler's finally must not re-arm installation.
        coordinator.FinishCloseAttempt();
        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.Close);
    }

    [Fact]
    public void CancelledWaitWithInstallationUnscheduledAllowsTheNextClose()
    {
        var coordinator = new UpdateCloseCoordinator();
        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.InstallOnExit);
        coordinator.FinishCloseAttempt();

        coordinator.RequestClose(true, false).ShouldBe(UpdateCloseAction.Close);
    }

    [Fact]
    public void CloseDuringCancellationCanStillEscapeBeforeTheOldHandlerFinishes()
    {
        var coordinator = new UpdateCloseCoordinator();
        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.InstallOnExit);

        coordinator.RequestClose(true, false).ShouldBe(UpdateCloseAction.CancelInstallationAndClose);
        coordinator.RequestClose(true, false).ShouldBe(UpdateCloseAction.Close);
    }

    [Fact]
    public void InstallerHandoffOrDeclinedConsentClosesWithoutStartingAnotherInstall()
    {
        var coordinator = new UpdateCloseCoordinator();
        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.InstallOnExit);
        coordinator.PermitClose();

        coordinator.RequestClose(true, true).ShouldBe(UpdateCloseAction.Close);
    }
}
