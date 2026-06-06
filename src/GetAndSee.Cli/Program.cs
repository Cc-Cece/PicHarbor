namespace GetAndSee.Cli;

/// <summary>
/// Entry point. The full <c>System.CommandLine</c> wiring (the <c>copy</c> verb) lands in Phase 4;
/// this placeholder keeps the executable buildable while the core pipeline is assembled.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("get-and-see — core pipeline under construction (Sprint 1).");
        return 0;
    }
}
