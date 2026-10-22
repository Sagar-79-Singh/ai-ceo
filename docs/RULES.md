
# Execution Rules

1. **NO PHASES**: Build the entire project structure, backend, and frontend in one single pass. Do not stop to ask questions.
2. **NO PYTHON/NODE.JS**: Use C# (.NET 8) exclusively. Do not use LangChain, CrewAI, or any external agent framework. Write a custom orchestrator using the `OpenAI-DotNet` NuGet package.
3. **OMNIROUTE SETUP**: All LLM calls must use the `OpenAI-DotNet` client configured as:
   ```csharp
   var client = new OpenAIClient(
       new ApiKeyCredential(Environment.GetEnvironmentVariable("OMNIROUTE_KEY") ?? "sk-48f9a7b2051199e0-64bc90-6a0ec9ee"),
       new OpenAIClientOptions() { Endpoint = new Uri("http://localhost:20128/v1") }
   );

4. STRICT PARSING: The CEO's planning output must be parsed safely. Use System.Text.Json to deserialize. Wrap in try/catch and fail gracefully.

5. SEQUENTIAL EXECUTION: Use a foreach loop for Worker execution. Do NOT use Parallel.ForEachAsync or Task.WhenAll. Wait for Worker A to finish before starting Worker B.

6. DATABASE: Use Entity Framework Core with SQLite. Run dotnet ef database update to create the DB.
