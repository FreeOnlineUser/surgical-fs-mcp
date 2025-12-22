---
license: mit
tags:
  - mcp
  - claude
  - filesystem
  - windows
  - dotnet
language:
  - en
---

# Surgical Filesystem MCP Server (C#)

A C# MCP server providing whitespace-tolerant file editing tools.

**Direct port of ClipMicro's battle-tested code**, including:

- 6-strategy matching cascade from `UpdateFindReplaceWithResult`
- ACID backup/rollback from `BackupManager`

Same code. Same behavior. No translation bugs.

## Quick Start (Pre-built Binaries)

Download from the `release/` folder:

- `surgical-fs-mcp.exe` - Standalone executable
- `surgical-fs-mcp.dll` - For use with `dotnet`
- `ModelContextProtocol.dll` - Required dependency

No build required - just configure Claude Desktop (see below).

## Features

### 6-Strategy Whitespace-Tolerant Matching

Directly ported from ClipMicro's `UpdateFindReplaceWithResult`:

| Strategy | What it handles |
|----------|-----------------|
| 1. Exact match | Perfect input |
| 2. Line ending normalization | CRLF vs LF vs CR |
| 3. Trim whitespace | Leading/trailing whitespace |
| 4. Tab normalization | Tabs → 4 spaces |
| 5. Fuzzy whitespace | Collapse `\s+` → single space, line-by-line |
| 6. Partial line match | Single-line substring matching |

### ACID Transaction Support

Directly ported from ClipMicro's `BackupManager`:

- **Pre-operation backup**: Files backed up to `.surgicalfs_backup_YYYYMMDD_HHMMSS/`
- **Automatic rollback**: On ANY failure, original files restored
- **New file tracking**: Files created during failed transactions are deleted
- **New directory tracking**: Empty directories created during failed transactions are removed
- **Batch transactions**: Multiple edits as one atomic operation

## Tools

| Tool | Description |
|------|-------------|
| `SurgicalEdit` | Find/replace with 6-strategy matching + ACID |
| `EditLines` | Replace lines by number + ACID |
| `InsertLines` | Insert at line without replacing + ACID |
| `DeleteLines` | Remove line range + ACID |
| `PreviewEdit` | Show diff without applying (read-only) |
| `ReadFileLines` | Read file with line numbers (read-only) |
| `BatchEdit` | Multiple edits as single ACID transaction |

## Installation

### Prerequisites
- .NET 8.0 SDK
- Claude Desktop

### Build

```bash
cd C:\Users\bradc\Documents\ClaudeWorkspace\Projects\surgical-fs-mcp
dotnet restore
dotnet build --configuration Release
```

### Configure Allowed Directories

Edit `SurgicalFsMcp.cs` and modify `AllowedDirectories` in the `PathValidator` class:

```csharp
private static readonly List<string> AllowedDirectories = new()
{
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects"),
    @"C:\Your\Custom\Path",
};
```

Rebuild after changing.

### Configure Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

**Option 1: Using dotnet run (easier for development)**
```json
{
  "mcpServers": {
    "surgical_fs": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\Users\\bradc\\Documents\\ClaudeWorkspace\\Projects\\surgical-fs-mcp\\SurgicalFsMcp.csproj"]
    }
  }
}
```

**Option 2: Using compiled exe (faster startup)**
```json
{
  "mcpServers": {
    "surgical_fs": {
      "command": "C:\\Users\\bradc\\Documents\\ClaudeWorkspace\\Projects\\surgical-fs-mcp\\bin\\Release\\net8.0\\surgical-fs-mcp.exe"
    }
  }
}
```

Restart Claude Desktop after configuring.

## Usage Examples

### Single Surgical Edit
```
SurgicalEdit(
    path: "config.json",
    find: '"debug": false',
    replace: '"debug": true'
)
```
Returns: `✅ Success: Edit applied using 'exact_match' matching`

### Preview Before Editing
```
PreviewEdit(
    path: "config.json",
    find: '"debug": false',
    replace: '"debug": true'
)
```
Returns a diff showing what would change.

### Batch Edit (ACID Transaction)
```
BatchEdit(edits_json: '[
    {"path": "file1.cs", "find": "oldMethod", "replace": "newMethod"},
    {"path": "file2.cs", "find": "oldMethod", "replace": "newMethod"},
    {"path": "file3.cs", "find": "oldMethod", "replace": "newMethod"}
]')
```
If ANY edit fails, ALL changes roll back automatically.

### Line-Based Editing
```
ReadFileLines(path: "code.cs", start_line: 50, end_line: 60)
EditLines(path: "code.cs", start_line: 52, end_line: 55, new_content: "// new code here")
```

## How ACID Rollback Works

1. **Before any modification**: Original file content is copied to backup directory
2. **During operation**: Changes are applied to the original file
3. **On success**: Backup directory is deleted
4. **On failure**: 
   - Original content is restored from backup
   - Any newly created files are deleted
   - Any newly created empty directories are removed
   - Error message includes "rolled back" confirmation

## Security

- Path validation against allowed directories
- Directory traversal (`..`) blocked
- UTF-8 encoding enforced
- Backup directories automatically cleaned up

## Credits

- 6-strategy matching: Direct port from ClipMicro's `UpdateFindReplaceWithResult`
- ACID backup system: Direct port from ClipMicro's `BackupManager`
- Original ClipMicro: [clipmicro.com](https://clipmicro.com)
