using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SurgicalFsMcp
{
    /// <summary>
    /// Surgical Filesystem MCP Server
    /// 
    /// Provides whitespace-tolerant file editing tools based on ClipMicro's 
    /// battle-tested 6-strategy matching cascade, with ACID transaction support.
    /// 
    /// Direct port from ClipMicro - same code, same behavior.
    /// </summary>
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            
            // CRITICAL: Route all logs to stderr so stdout is clean for JSON-RPC
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();
            
            await builder.Build().RunAsync();
        }
    }

    // ===============================================
    // RESULT CLASSES (from ClipMicro)
    // ===============================================

    public class UpdateResult
    {
        public bool Success { get; set; }
        public string Strategy { get; set; } = "";
        public string NewContent { get; set; } = "";
        public string Reason { get; set; } = "";
        public string Suggestion { get; set; } = "";
    }

    public class TransactionResult
    {
        public bool Success { get; set; }
        public List<string> SuccessfulOperations { get; set; } = new List<string>();
        public List<string> FailedOperations { get; set; } = new List<string>();
        public string FirstFailureReason { get; set; } = "";
        public int OperationsProcessed { get; set; }
        public int TotalOperations { get; set; }
        public bool BackupCreated { get; set; }
        public bool RollbackPerformed { get; set; }
    }

    // ===============================================
    // BACKUP MANAGER (Direct port from ClipMicro)
    // ===============================================

    /// <summary>
    /// ACID Transaction Manager - Direct port from ClipMicro's BackupManager
    /// Provides database-level transaction guarantees for file operations.
    /// </summary>
    public class BackupManager : IDisposable
    {
        private string backupDirectory;
        private Dictionary<string, string> backedUpFiles; // original path -> backup path
        private HashSet<string> newlyCreatedFiles; // track files created during transaction
        private HashSet<string> newlyCreatedDirectories; // track directories created during transaction
        private DateTime backupTimestamp;
        private bool disposed = false;

        public BackupManager(string workingDirectory)
        {
            backupTimestamp = DateTime.Now;
            backupDirectory = Path.Combine(workingDirectory, $".surgicalfs_backup_{backupTimestamp:yyyyMMdd_HHmmss}");
            backedUpFiles = new Dictionary<string, string>();
            newlyCreatedFiles = new HashSet<string>();
            newlyCreatedDirectories = new HashSet<string>();
        }

        public bool CreateBackup(string filePath, string workingDirectory)
        {
            try
            {
                string fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(workingDirectory, filePath);
                string relativePath = GetRelativePath(workingDirectory, fullPath);
                
                // Only backup if file exists
                if (!File.Exists(fullPath))
                {
                    return true; // No backup needed for non-existent files
                }

                // Create backup directory if it doesn't exist
                if (!Directory.Exists(backupDirectory))
                {
                    Directory.CreateDirectory(backupDirectory);
                }

                // Create backup file path maintaining directory structure
                string backupFilePath = Path.Combine(backupDirectory, relativePath);
                string backupFileDirectory = Path.GetDirectoryName(backupFilePath);
                
                if (!string.IsNullOrEmpty(backupFileDirectory) && !Directory.Exists(backupFileDirectory))
                {
                    Directory.CreateDirectory(backupFileDirectory);
                }

                // Copy file to backup location
                File.Copy(fullPath, backupFilePath, overwrite: true);
                backedUpFiles[fullPath] = backupFilePath;
                
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Backup failed for {filePath}: {ex.Message}");
                return false;
            }
        }

        public bool RestoreAllBackups(string workingDirectory)
        {
            bool allRestored = true;
            var restorationErrors = new List<string>();

            // Restore modified files from backup
            foreach (var kvp in backedUpFiles)
            {
                try
                {
                    string originalPath = kvp.Key;
                    string backupPath = kvp.Value;

                    if (File.Exists(backupPath))
                    {
                        // Ensure directory exists
                        string originalDirectory = Path.GetDirectoryName(originalPath);
                        if (!string.IsNullOrEmpty(originalDirectory) && !Directory.Exists(originalDirectory))
                        {
                            Directory.CreateDirectory(originalDirectory);
                        }

                        File.Copy(backupPath, originalPath, overwrite: true);
                    }
                }
                catch (Exception ex)
                {
                    allRestored = false;
                    restorationErrors.Add($"Failed to restore {kvp.Key}: {ex.Message}");
                }
            }

            // Delete newly created files
            foreach (string filePath in newlyCreatedFiles)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        System.Diagnostics.Debug.WriteLine($"Deleted newly created file during rollback: {filePath}");
                    }
                }
                catch (Exception ex)
                {
                    allRestored = false;
                    restorationErrors.Add($"Failed to delete newly created file {filePath}: {ex.Message}");
                }
            }

            // Delete newly created directories (in reverse order - deepest first)
            var sortedDirectories = newlyCreatedDirectories.OrderByDescending(d => d.Length).ToList();
            foreach (string dirPath in sortedDirectories)
            {
                try
                {
                    if (Directory.Exists(dirPath))
                    {
                        // Only delete if directory is empty
                        if (!Directory.EnumerateFileSystemEntries(dirPath).Any())
                        {
                            Directory.Delete(dirPath);
                            System.Diagnostics.Debug.WriteLine($"Deleted newly created empty directory during rollback: {dirPath}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"Skipped directory deletion (not empty): {dirPath}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    allRestored = false;
                    restorationErrors.Add($"Failed to delete newly created directory {dirPath}: {ex.Message}");
                }
            }

            if (!allRestored)
            {
                System.Diagnostics.Debug.WriteLine($"Some files could not be restored: {string.Join("; ", restorationErrors)}");
            }

            return allRestored;
        }

        public void CleanupBackups()
        {
            try
            {
                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, recursive: true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cleanup backups: {ex.Message}");
            }
        }

        public void TrackNewFile(string filePath)
        {
            newlyCreatedFiles.Add(filePath);
        }

        public void TrackNewDirectory(string directoryPath)
        {
            newlyCreatedDirectories.Add(directoryPath);
        }

        public int BackupCount => backedUpFiles.Count;
        public int NewFileCount => newlyCreatedFiles.Count;
        public int NewDirectoryCount => newlyCreatedDirectories.Count;

        private string GetRelativePath(string basePath, string fullPath)
        {
            if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                string result = fullPath.Substring(basePath.Length);
                return result.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return Path.GetFileName(fullPath);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                CleanupBackups();
                disposed = true;
            }
        }
    }

    // ===============================================
    // MATCHING ENGINE (Direct port from ClipMicro)
    // ===============================================

    /// <summary>
    /// ClipMicro's 6-strategy cascade for whitespace-tolerant find/replace.
    /// This is a DIRECT PORT from ClipMicro's UpdateFindReplaceWithResult method.
    /// </summary>
    public static class MatchingEngine
    {
        public static UpdateResult FindAndReplace(string content, string find, string replace)
        {
            try
            {
                // Strategy 1: Exact match (original behavior)
                if (content.Contains(find))
                {
                    string newContent = content.Replace(find, replace);
                    return new UpdateResult { Success = true, Strategy = "exact_match", NewContent = newContent };
                }

                // Strategy 2: Normalize line endings and try again
                string normalizedContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
                string normalizedFind = find.Replace("\r\n", "\n").Replace("\r", "\n");

                if (normalizedContent.Contains(normalizedFind))
                {
                    string newContent = normalizedContent.Replace(normalizedFind, replace);
                    return new UpdateResult { Success = true, Strategy = "line_ending_normalization", NewContent = newContent };
                }

                // Strategy 3: Trim whitespace from find pattern and try fuzzy matching
                string trimmedFind = find.Trim();
                if (trimmedFind.Length > 0 && normalizedContent.Contains(trimmedFind))
                {
                    string newContent = normalizedContent.Replace(trimmedFind, replace.Trim());
                    return new UpdateResult { Success = true, Strategy = "trimmed_whitespace", NewContent = newContent };
                }

                // Strategy 4: Handle mixed tab/space indentation
                string tabNormalizedContent = normalizedContent.Replace("\t", "    "); // 4 spaces per tab
                string tabNormalizedFind = normalizedFind.Replace("\t", "    ");

                if (tabNormalizedContent.Contains(tabNormalizedFind))
                {
                    string newContent = tabNormalizedContent.Replace(tabNormalizedFind, replace);
                    return new UpdateResult { Success = true, Strategy = "tab_normalization", NewContent = newContent };
                }

                // Strategy 5: Try with space-normalized find (all whitespace -> single space)
                string spaceNormalizedFind = Regex.Replace(trimmedFind, @"\s+", " ");
                string spaceNormalizedContent = Regex.Replace(normalizedContent, @"\s+", " ");

                if (spaceNormalizedContent.Contains(spaceNormalizedFind))
                {
                    // Find the original text in the content and replace it
                    string[] contentLines = normalizedContent.Split('\n');
                    string[] findLines = normalizedFind.Split('\n');

                    for (int i = 0; i <= contentLines.Length - findLines.Length; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < findLines.Length; j++)
                        {
                            string contentLineNormalized = Regex.Replace(contentLines[i + j].Trim(), @"\s+", " ");
                            string findLineNormalized = Regex.Replace(findLines[j].Trim(), @"\s+", " ");

                            if (contentLineNormalized != findLineNormalized)
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
                        {
                            // Replace the matched lines
                            List<string> newLines = new List<string>(contentLines);
                            newLines.RemoveRange(i, findLines.Length);

                            string[] replaceLines = replace.Split('\n');
                            newLines.InsertRange(i, replaceLines);

                            string newContent = string.Join("\n", newLines);
                            return new UpdateResult { Success = true, Strategy = "fuzzy_whitespace", NewContent = newContent };
                        }
                    }
                }

                // Strategy 6: Partial line matching for single-line finds
                if (!normalizedFind.Contains('\n'))
                {
                    string[] contentLines = normalizedContent.Split('\n');
                    for (int i = 0; i < contentLines.Length; i++)
                    {
                        if (contentLines[i].Trim().Contains(trimmedFind))
                        {
                            contentLines[i] = contentLines[i].Replace(contentLines[i].Trim(), replace.Trim());
                            string newContent = string.Join("\n", contentLines);
                            return new UpdateResult { Success = true, Strategy = "partial_line_match", NewContent = newContent };
                        }
                    }
                }

                // All strategies failed - provide helpful error message
                string reason = "Text not found in file";
                string suggestion = "Check that the FIND text matches exactly (case-sensitive)";

                if (find.Contains("\""))
                {
                    suggestion = "Check quotes and formatting - FIND text must match exactly";
                }
                else if (find.Length < 5)
                {
                    suggestion = "Try using a longer, more unique text pattern to find";
                }
                else if (find.Contains("\n"))
                {
                    suggestion = "For multi-line FIND, ensure line breaks and indentation match exactly";
                }

                return new UpdateResult
                {
                    Success = false,
                    Reason = reason,
                    Suggestion = suggestion
                };
            }
            catch (Exception ex)
            {
                return new UpdateResult
                {
                    Success = false,
                    Reason = $"Update error: {ex.Message}",
                    Suggestion = "Check file permissions and try again"
                };
            }
        }
    }

    // ===============================================
    // PATH VALIDATION
    // ===============================================

    public static class PathValidator
    {
        // Configure allowed directories here
        private static readonly List<string> AllowedDirectories = new()
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects"),
            @"C:\Users\bradc\Documents\ClaudeWorkspace",
        };

        public static bool IsPathAllowed(string path)
        {
            string absPath = Path.GetFullPath(path);
            if (path.Contains("..")) return false;

            foreach (var allowed in AllowedDirectories)
            {
                if (absPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static string ValidatePath(string path)
        {
            string absPath = Path.GetFullPath(path);
            if (!IsPathAllowed(absPath))
            {
                throw new UnauthorizedAccessException(
                    $"Path '{path}' is outside allowed directories. " +
                    $"Allowed: {string.Join(", ", AllowedDirectories)}");
            }
            return absPath;
        }

        public static string GetWorkingDirectory(string path)
        {
            string absPath = Path.GetFullPath(path);
            foreach (var allowed in AllowedDirectories)
            {
                if (absPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                    return allowed;
            }
            return Path.GetDirectoryName(absPath) ?? absPath;
        }
    }

    // ===============================================
    // MCP TOOLS
    // ===============================================

    [McpServerToolType]
    public static class SurgicalFsTools
    {
        [McpServerTool, Description("PREFERRED for file edits. Whitespace-tolerant find/replace that handles mismatched indentation, tabs vs spaces, and line endings automatically. Includes ACID backup with automatic rollback on failure. Use this instead of filesystem:edit_file.")]
        public static string SurgicalEdit(
            [Description("Path to the file to edit")] string path,
            [Description("Text to find (whitespace-tolerant matching)")] string find,
            [Description("Text to replace with (can be empty to delete)")] string replace)
        {
            BackupManager? backupManager = null;
            
            try
            {
                string absPath = PathValidator.ValidatePath(path);
                string workingDir = PathValidator.GetWorkingDirectory(absPath);

                if (!File.Exists(absPath))
                    return $"Error: File not found: {path}";

                // Create backup before modification
                backupManager = new BackupManager(workingDir);
                if (!backupManager.CreateBackup(absPath, workingDir))
                {
                    return "Error: Failed to create backup before modification";
                }

                string content = File.ReadAllText(absPath, Encoding.UTF8);
                var result = MatchingEngine.FindAndReplace(content, find, replace);

                if (result.Success)
                {
                    File.WriteAllText(absPath, result.NewContent, new UTF8Encoding(false));
                    backupManager.CleanupBackups(); // Success - remove backup
                    return $"✅ Success: Edit applied using '{result.Strategy}' matching";
                }
                else
                {
                    // No changes made, cleanup backup
                    backupManager.CleanupBackups();
                    return $"❌ {result.Reason}\n💡 Suggestion: {result.Suggestion}";
                }
            }
            catch (Exception ex)
            {
                // Rollback on any error
                if (backupManager != null && backupManager.BackupCount > 0)
                {
                    backupManager.RestoreAllBackups(PathValidator.GetWorkingDirectory(path));
                    return $"Error (rolled back): {ex.Message}";
                }
                return $"Error: {ex.Message}";
            }
            finally
            {
                backupManager?.Dispose();
            }
        }

        [McpServerTool, Description("PREFERRED for replacing code blocks. Replace lines by number with ACID rollback. Use ReadFileLines first to see line numbers, then this to replace. Safer than filesystem:edit_file.")]
        public static string EditLines(
            [Description("Path to the file to edit")] string path,
            [Description("First line to replace (1-indexed)")] int start_line,
            [Description("Last line to replace (1-indexed, inclusive)")] int end_line,
            [Description("Content to replace the lines with")] string new_content)
        {
            BackupManager? backupManager = null;
            
            try
            {
                string absPath = PathValidator.ValidatePath(path);
                string workingDir = PathValidator.GetWorkingDirectory(absPath);

                if (!File.Exists(absPath))
                    return $"Error: File not found: {path}";

                if (end_line < start_line)
                    return $"Error: end_line ({end_line}) must be >= start_line ({start_line})";

                backupManager = new BackupManager(workingDir);
                if (!backupManager.CreateBackup(absPath, workingDir))
                {
                    return "Error: Failed to create backup before modification";
                }

                var lines = new List<string>(File.ReadAllLines(absPath, Encoding.UTF8));
                int totalLines = lines.Count;

                if (start_line > totalLines)
                {
                    backupManager.CleanupBackups();
                    return $"Error: start_line ({start_line}) exceeds file length ({totalLines} lines)";
                }

                end_line = Math.Min(end_line, totalLines);
                int startIdx = start_line - 1;
                int count = end_line - start_line + 1;

                var newLines = new_content.Split('\n');
                lines.RemoveRange(startIdx, count);
                lines.InsertRange(startIdx, newLines);

                File.WriteAllLines(absPath, lines, new UTF8Encoding(false));
                backupManager.CleanupBackups();

                return $"✅ Success: Replaced lines {start_line}-{end_line} ({count} lines) with {newLines.Length} new lines";
            }
            catch (Exception ex)
            {
                if (backupManager != null && backupManager.BackupCount > 0)
                {
                    backupManager.RestoreAllBackups(PathValidator.GetWorkingDirectory(path));
                    return $"Error (rolled back): {ex.Message}";
                }
                return $"Error: {ex.Message}";
            }
            finally
            {
                backupManager?.Dispose();
            }
        }

        [McpServerTool, Description("Insert content at a line without replacing existing content. Use line_number=0 to prepend, or > file length to append. Has ACID rollback.")]
        public static string InsertLines(
            [Description("Path to the file to edit")] string path,
            [Description("Line to insert before (1-indexed, 0=prepend)")] int line_number,
            [Description("Content to insert")] string content)
        {
            BackupManager? backupManager = null;
            
            try
            {
                string absPath = PathValidator.ValidatePath(path);
                string workingDir = PathValidator.GetWorkingDirectory(absPath);

                if (!File.Exists(absPath))
                    return $"Error: File not found: {path}";

                backupManager = new BackupManager(workingDir);
                if (!backupManager.CreateBackup(absPath, workingDir))
                {
                    return "Error: Failed to create backup before modification";
                }

                var lines = new List<string>(File.ReadAllLines(absPath, Encoding.UTF8));
                var newLines = content.Split('\n');
                string position;

                if (line_number == 0)
                {
                    lines.InsertRange(0, newLines);
                    position = "at beginning";
                }
                else if (line_number > lines.Count)
                {
                    lines.AddRange(newLines);
                    position = "at end";
                }
                else
                {
                    lines.InsertRange(line_number - 1, newLines);
                    position = $"before line {line_number}";
                }

                File.WriteAllLines(absPath, lines, new UTF8Encoding(false));
                backupManager.CleanupBackups();

                return $"✅ Success: Inserted {newLines.Length} lines {position}";
            }
            catch (Exception ex)
            {
                if (backupManager != null && backupManager.BackupCount > 0)
                {
                    backupManager.RestoreAllBackups(PathValidator.GetWorkingDirectory(path));
                    return $"Error (rolled back): {ex.Message}";
                }
                return $"Error: {ex.Message}";
            }
            finally
            {
                backupManager?.Dispose();
            }
        }

        [McpServerTool, Description("Delete lines by range. 1-indexed, inclusive. Has ACID rollback.")]
        public static string DeleteLines(
            [Description("Path to the file to edit")] string path,
            [Description("First line to delete (1-indexed)")] int start_line,
            [Description("Last line to delete (1-indexed, inclusive)")] int end_line)
        {
            BackupManager? backupManager = null;
            
            try
            {
                string absPath = PathValidator.ValidatePath(path);
                string workingDir = PathValidator.GetWorkingDirectory(absPath);

                if (!File.Exists(absPath))
                    return $"Error: File not found: {path}";

                if (end_line < start_line)
                    return $"Error: end_line ({end_line}) must be >= start_line ({start_line})";

                backupManager = new BackupManager(workingDir);
                if (!backupManager.CreateBackup(absPath, workingDir))
                {
                    return "Error: Failed to create backup before modification";
                }

                var lines = new List<string>(File.ReadAllLines(absPath, Encoding.UTF8));
                int totalLines = lines.Count;

                if (start_line > totalLines)
                {
                    backupManager.CleanupBackups();
                    return $"Error: start_line ({start_line}) exceeds file length ({totalLines} lines)";
                }

                end_line = Math.Min(end_line, totalLines);
                int startIdx = start_line - 1;
                int count = end_line - start_line + 1;

                lines.RemoveRange(startIdx, count);
                File.WriteAllLines(absPath, lines, new UTF8Encoding(false));
                backupManager.CleanupBackups();

                return $"✅ Success: Deleted lines {start_line}-{end_line} ({count} lines)";
            }
            catch (Exception ex)
            {
                if (backupManager != null && backupManager.BackupCount > 0)
                {
                    backupManager.RestoreAllBackups(PathValidator.GetWorkingDirectory(path));
                    return $"Error (rolled back): {ex.Message}";
                }
                return $"Error: {ex.Message}";
            }
            finally
            {
                backupManager?.Dispose();
            }
        }

        [McpServerTool, Description("Preview a SurgicalEdit without applying. Shows diff. Use to verify changes before committing.")]
        public static string PreviewEdit(
            [Description("Path to the file")] string path,
            [Description("Text to find")] string find,
            [Description("Text to replace with")] string replace)
        {
            try
            {
                string absPath = PathValidator.ValidatePath(path);

                if (!File.Exists(absPath))
                    return $"Error: File not found: {path}";

                string content = File.ReadAllText(absPath, Encoding.UTF8);
                var result = MatchingEngine.FindAndReplace(content, find, replace);

                if (!result.Success)
                    return $"❌ {result.Reason}\n💡 Suggestion: {result.Suggestion}";

                var originalLines = content.Split('\n');
                var newLines = result.NewContent.Split('\n');

                var diff = new StringBuilder();
                diff.AppendLine($"Strategy: {result.Strategy}");
                diff.AppendLine($"--- a/{Path.GetFileName(path)}");
                diff.AppendLine($"+++ b/{Path.GetFileName(path)}");

                int maxLines = Math.Max(originalLines.Length, newLines.Length);
                for (int i = 0; i < maxLines; i++)
                {
                    string orig = i < originalLines.Length ? originalLines[i] : "";
                    string newL = i < newLines.Length ? newLines[i] : "";

                    if (orig != newL)
                    {
                        if (i < originalLines.Length)
                            diff.AppendLine($"-{orig}");
                        if (i < newLines.Length)
                            diff.AppendLine($"+{newL}");
                    }
                }

                return diff.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("PREFERRED for viewing files. Shows content with line numbers. Use before EditLines or DeleteLines to identify line numbers.")]
        public static string ReadFileLines(
            [Description("Path to the file to read")] string path,
            [Description("First line to read (1-indexed, optional)")] int? start_line = null,
            [Description("Last line to read (1-indexed, optional)")] int? end_line = null)
        {
            try
            {
                string absPath = PathValidator.ValidatePath(path);

                if (!File.Exists(absPath))
                    return $"Error: File not found: {path}";

                var lines = File.ReadAllLines(absPath, Encoding.UTF8);
                int totalLines = lines.Length;

                int start = (start_line ?? 1) - 1;
                int end = end_line ?? totalLines;

                start = Math.Max(0, Math.Min(start, totalLines));
                end = Math.Max(start, Math.Min(end, totalLines));

                var output = new StringBuilder();
                output.AppendLine($"File: {path} (lines {start + 1}-{end} of {totalLines})");
                output.AppendLine(new string('=', 50));

                int width = end.ToString().Length;
                for (int i = start; i < end; i++)
                {
                    output.AppendLine($"{(i + 1).ToString().PadLeft(width)} | {lines[i]}");
                }

                return output.ToString();
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }

        [McpServerTool, Description("PREFERRED for multi-file refactoring. Multiple edits as one atomic transaction - all succeed or all roll back. Safer than multiple filesystem:edit_file calls.")]
        public static string BatchEdit(
            [Description("JSON array of edit operations: [{\"path\":\"...\",\"find\":\"...\",\"replace\":\"...\"}]")] string edits_json)
        {
            BackupManager? backupManager = null;
            string? workingDir = null;
            
            try
            {
                var edits = JsonSerializer.Deserialize<List<EditOperation>>(edits_json);

                if (edits == null || edits.Count == 0)
                    return "Error: No edits provided";

                // Validate all paths first
                var validatedPaths = new List<string>();
                foreach (var edit in edits)
                {
                    string absPath = PathValidator.ValidatePath(edit.Path);
                    if (!File.Exists(absPath))
                        return $"Error: File not found: {edit.Path}";
                    validatedPaths.Add(absPath);
                }

                // Determine working directory from first file
                workingDir = PathValidator.GetWorkingDirectory(validatedPaths[0]);
                backupManager = new BackupManager(workingDir);

                // Create backups for all files
                for (int i = 0; i < validatedPaths.Count; i++)
                {
                    if (!backupManager.CreateBackup(validatedPaths[i], workingDir))
                    {
                        backupManager.CleanupBackups();
                        return $"Error: Failed to create backup for {edits[i].Path}";
                    }
                }

                // Execute all edits
                var results = new List<string>();
                for (int i = 0; i < edits.Count; i++)
                {
                    string content = File.ReadAllText(validatedPaths[i], Encoding.UTF8);
                    var result = MatchingEngine.FindAndReplace(content, edits[i].Find, edits[i].Replace);

                    if (!result.Success)
                    {
                        // Rollback all changes
                        backupManager.RestoreAllBackups(workingDir);
                        return $"❌ Batch failed on {edits[i].Path}: {result.Reason}\n💡 All changes rolled back\n💡 Suggestion: {result.Suggestion}";
                    }

                    File.WriteAllText(validatedPaths[i], result.NewContent, new UTF8Encoding(false));
                    results.Add($"✅ {Path.GetFileName(edits[i].Path)}: {result.Strategy}");
                }

                // Success - cleanup backups
                backupManager.CleanupBackups();

                var output = new StringBuilder();
                output.AppendLine($"✅ Batch complete: {edits.Count} edits applied");
                foreach (var r in results)
                    output.AppendLine($"  {r}");
                
                return output.ToString();
            }
            catch (Exception ex)
            {
                if (backupManager != null && workingDir != null && backupManager.BackupCount > 0)
                {
                    backupManager.RestoreAllBackups(workingDir);
                    return $"❌ Batch failed, all changes rolled back: {ex.Message}";
                }
                return $"Error: {ex.Message}";
            }
            finally
            {
                backupManager?.Dispose();
            }
        }
    }

    public class EditOperation
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("find")]
        public string Find { get; set; } = "";

        [JsonPropertyName("replace")]
        public string Replace { get; set; } = "";
    }
}
