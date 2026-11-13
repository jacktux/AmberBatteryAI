using System.Runtime.InteropServices;
using System.Text.Json;

namespace AmberBatteryAI.Services;

/// <summary>
/// Provides persistent file-based storage for custom AI prompts.
/// Stores prompts in a JSON file to survive application restarts.
/// </summary>
public class PromptStorageService
{
    private readonly string _filePath;
    private readonly ILogger<PromptStorageService> _logger;
    private readonly object _lockObject = new();

    public PromptStorageService(ILogger<PromptStorageService> logger)
    {
        _logger = logger;

        // Use a data directory appropriate for the environment
        string dataDir;

        // Check if running in a container/Home Assistant environment
        if (Directory.Exists("/data"))
        {
            // Home Assistant add-on data directory
            dataDir = "/data/amber-battery-ai";
        }
        else if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production" && 
                 !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Linux production environment (not Home Assistant)
            dataDir = "/var/lib/amber-battery-ai";
        }
        else
        {
            // Development or Windows
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AmberBatteryAI");
        }

        // Create directory safely
        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create data directory {DataDir}, using temp directory", dataDir);
            dataDir = Path.Combine(Path.GetTempPath(), "AmberBatteryAI");
            Directory.CreateDirectory(dataDir);
        }

        _filePath = Path.Combine(dataDir, "prompt_storage.json");
        _logger.LogInformation("Prompt storage path: {FilePath}", _filePath);
    }

    /// <summary>
    /// Load the custom prompt from persistent storage.
    /// </summary>
    public string? LoadPrompt()
    {
        lock (_lockObject)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _logger.LogInformation("No saved prompt found at {FilePath}", _filePath);
                    return null;
                }

                string json = File.ReadAllText(_filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("customPrompt", out var promptProp))
                {
                    string? prompt = promptProp.GetString();
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        _logger.LogInformation("Loaded custom prompt from {FilePath} ({Length} chars)", _filePath, prompt.Length);
                        return prompt;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load prompt from {FilePath}", _filePath);
                return null;
            }
        }
    }

    /// <summary>
    /// Save the custom prompt to persistent storage.
    /// Pass null or empty string to clear the custom prompt.
    /// </summary>
    public bool SavePrompt(string? prompt)
    {
        lock (_lockObject)
        {
            try
            {
                var data = new { customPrompt = prompt };
                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    _logger.LogInformation("Cleared custom prompt file at {FilePath}", _filePath);
                }
                else
                {
                    _logger.LogInformation("Saved custom prompt to {FilePath} ({Length} chars)", _filePath, prompt.Length);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save prompt to {FilePath}", _filePath);
                return false;
            }
        }
    }

    /// <summary>
    /// Delete the prompt storage file.
    /// </summary>
    public bool DeletePromptFile()
    {
        lock (_lockObject)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                    _logger.LogInformation("Deleted prompt storage file at {FilePath}", _filePath);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete prompt file at {FilePath}", _filePath);
                return false;
            }
        }
    }

    /// <summary>
    /// Get the full file path for the storage file (for diagnostics).
    /// </summary>
    public string GetStoragePath() => _filePath;
}
