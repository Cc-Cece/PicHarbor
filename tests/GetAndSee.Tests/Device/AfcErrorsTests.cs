using GetAndSee.Core.Device;
using GetAndSee.Core.Errors;
using iMobileDevice.Afc;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Device;

/// <summary>
/// Pins the conservative boundary between a lost device <i>connection</i> (stop the run resumably)
/// and a single unreadable file (fail it and keep going) — the classification that decides whether a
/// failed read-only AFC call aborts the whole copy or just one file (#25 #3).
/// </summary>
public sealed class AfcErrorsTests
{
    [Theory]
    [InlineData(AfcError.MuxError)]
    [InlineData(AfcError.ServiceNotConnected)]
    [InlineData(AfcError.ServiceClientFailed)]
    public void Transport_failures_are_connection_fatal(AfcError error)
    {
        AfcErrors.IsConnectionFatal(error).ShouldBeTrue();
        AfcErrors.ToException(error, "ignored").ShouldBeOfType<DeviceConnectionLostException>();
    }

    [Theory]
    [InlineData(AfcError.ObjectNotFound)]
    [InlineData(AfcError.PermDenied)]
    [InlineData(AfcError.ReadError)]
    [InlineData(AfcError.ObjectIsDir)]
    [InlineData(AfcError.IoError)]
    [InlineData(AfcError.OpTimeout)]
    public void Per_file_errors_are_not_connection_fatal(AfcError error)
    {
        AfcErrors.IsConnectionFatal(error).ShouldBeFalse();

        DeviceException ex = AfcErrors.ToException(error, "could not read one file");
        ex.ShouldBeOfType<DeviceException>(); // a plain per-file failure, not a connection loss
        ex.Message.ShouldBe("could not read one file");
    }
}
