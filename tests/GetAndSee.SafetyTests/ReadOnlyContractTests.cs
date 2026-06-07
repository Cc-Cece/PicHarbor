using System.Collections.Generic;
using System.Linq;
using GetAndSee.Core.Device;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace GetAndSee.SafetyTests;

/// <summary>
/// Build-failing enforcement of the device read-only contract (PROJECT_BRIEF §9.1,
/// <c>docs/sprint-1/afc-library-decision.md</c> §5.5).
/// </summary>
/// <remarks>
/// <para>
/// <c>imobiledevice-net</c> is a full binding and exposes device-mutating calls. This fixture
/// reflects over the <i>compiled</i> <c>GetAndSee.Core</c> assembly with Mono.Cecil and fails the
/// build if any AFC/lockdown write, delete, rename, truncate, link, or directory-create symbol is
/// referenced — or if <c>afc_file_open</c> is ever invoked in a write/append mode.
/// </para>
/// <para>
/// Why IL inspection and not plain reflection: C# inlines enum constants as integer literals, so a
/// write-mode open (<c>AfcFileMode.FopenWr</c>) leaves no symbolic trace in the metadata. Only
/// scanning the IL for the mode constant at the call site can catch it.
/// </para>
/// </remarks>
public sealed class ReadOnlyContractTests
{
    /// <summary>
    /// AFC and lockdown methods that mutate the device. A reference to any of these anywhere in
    /// <c>GetAndSee.Core</c> is a contract violation. Source: decision doc §5.5.
    /// </summary>
    private static readonly string[] ForbiddenMethodNames =
    [
        // AFC mutators.
        "afc_file_write",
        "afc_truncate",
        "afc_file_truncate",
        "afc_remove_path",
        "afc_remove_path_and_contents",
        "afc_make_directory",
        "afc_make_link",
        "afc_rename_path",
        "afc_set_file_time",

        // lockdown state mutators.
        "lockdownd_set_value",
        "lockdownd_remove_value",
        "lockdownd_pair",
        "lockdownd_unpair",
        "lockdownd_activate",
        "lockdownd_deactivate",
    ];

    /// <summary>
    /// Integer values of the write/append <c>AfcFileMode</c> members. <c>FopenRdonly</c> (1) is the
    /// only permitted mode; 2..6 are <c>FopenRw</c>/<c>FopenWronly</c>/<c>FopenWr</c>/<c>FopenAppend</c>/<c>FopenRdappend</c>.
    /// </summary>
    private static readonly HashSet<int> ForbiddenOpenModes = [2, 3, 4, 5, 6];

    private const string AfcFileOpen = "afc_file_open";

    private static string CoreAssemblyPath => typeof(IPhoneClient).Assembly.Location;

    [Fact]
    public void Core_references_no_device_write_delete_or_rename_method()
    {
        var violations = new List<string>();
        int methodsScanned = 0;
        bool sawReadDirectory = false;

        using var module = ModuleDefinition.ReadModule(CoreAssemblyPath);
        foreach (MethodDefinition method in EnumerateMethodsWithBodies(module))
        {
            methodsScanned++;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference called)
                {
                    continue;
                }

                if (called.Name == "afc_read_directory")
                {
                    sawReadDirectory = true;
                }

                if (ForbiddenMethodNames.Contains(called.Name))
                {
                    violations.Add($"{method.DeclaringType.FullName}.{method.Name} -> {called.Name}");
                }
            }
        }

        // Guard against a false pass (wrong/empty assembly): we must have scanned real code that
        // actually uses the AFC read path.
        methodsScanned.ShouldBeGreaterThan(0, "no methods scanned — did the Core assembly resolve?");
        sawReadDirectory.ShouldBeTrue("expected the AFC read path (afc_read_directory) in GetAndSee.Core");

        violations.ShouldBeEmpty(
            "GetAndSee.Core must never reference a device write/delete/rename method. Found:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void afc_file_open_is_never_invoked_in_a_write_or_append_mode()
    {
        var violations = new List<string>();

        using var module = ModuleDefinition.ReadModule(CoreAssemblyPath);
        foreach (MethodDefinition method in EnumerateMethodsWithBodies(module))
        {
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference mr || mr.Name != AfcFileOpen)
                {
                    continue;
                }

                // The fileMode argument is the integer constant loaded just before the call
                // (immediately before the `ref handle` load). Walk back to the nearest constant,
                // stopping at a call boundary so we never grab a literal from another expression.
                int? mode = FindModeArgument(instruction);
                if (mode is int value && ForbiddenOpenModes.Contains(value))
                {
                    violations.Add($"{method.DeclaringType.FullName}.{method.Name} opens a file with mode {value}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "afc_file_open may only be used in read-only mode (FopenRdonly). Found:\n" +
            string.Join("\n", violations));
    }

    private static int? FindModeArgument(Instruction call)
    {
        for (Instruction? cursor = call.Previous; cursor is not null; cursor = cursor.Previous)
        {
            int? constant = TryGetInt32Constant(cursor);
            if (constant is not null)
            {
                return constant;
            }

            // A call/newobj before any constant means the mode is not a simple literal here;
            // stop rather than misattribute a constant from an earlier sub-expression.
            if (cursor.OpCode.FlowControl == FlowControl.Call)
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<MethodDefinition> EnumerateMethodsWithBodies(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (method.HasBody)
                {
                    yield return method;
                }
            }
        }
    }

    private static int? TryGetInt32Constant(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldc_I4_M1 => -1,
        Code.Ldc_I4_0 => 0,
        Code.Ldc_I4_1 => 1,
        Code.Ldc_I4_2 => 2,
        Code.Ldc_I4_3 => 3,
        Code.Ldc_I4_4 => 4,
        Code.Ldc_I4_5 => 5,
        Code.Ldc_I4_6 => 6,
        Code.Ldc_I4_7 => 7,
        Code.Ldc_I4_8 => 8,
        Code.Ldc_I4_S => (sbyte)instruction.Operand,
        Code.Ldc_I4 => (int)instruction.Operand,
        _ => null,
    };
}
