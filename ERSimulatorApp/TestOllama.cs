using ERSimulatorApp.Tests;

Console.WriteLine("🏥 ER Simulator - Ollama Integration Test");
Console.WriteLine("==========================================");
Console.WriteLine();

await OllamaConnectionTest.TestOllamaConnection();

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
