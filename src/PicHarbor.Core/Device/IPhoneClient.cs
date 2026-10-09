namespace PicHarbor.Core.Device;

/// <summary>
/// Read-only view of an iPhone's media file system over AFC (Apple File Conduit).
/// </summary>
/// <remarks>
/// <para>
/// This is the project's single most safety-critical contract. By design the interface exposes
/// <b>only</b> read operations — connect, list a directory, stat a file, and open a file for
/// reading. There is deliberately no write, delete, rename, truncate, or directory-create method
/// here or on any implementation, so no caller can mutate the device (PROJECT_BRIEF §9.1).
/// </para>
/// <para>
/// The prohibition is enforced at build time: <c>ReadOnlyContractTests</c> reflects over the
/// compiled <c>PicHarbor.Core</c> assembly and fails the build if any AFC/lockdown mutation symbol
/// is referenced (see <c>docs/sprint-1/afc-library-decision.md</c> §5.5).
/// </para>
/// </remarks>
public interface IPhoneClient : IMediaSourceClient
{
}
