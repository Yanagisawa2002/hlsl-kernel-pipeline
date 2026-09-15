namespace HlslPerf.CrowdExport;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: HlslPerf.CrowdExport <request.json> <new-output-directory>");
            return 2;
        }
        try
        {
            var receipt = CrowdExporter.Export(args[0], args[1]);
            Console.WriteLine($"CPU export completed: {receipt.Atlases.Count} atlases; receipt.json records input/output hashes. GPU execution and performance were not measured.");
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or
            System.Text.Json.JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
