# PHP Switcher

A simple C# console utility that helps you switch between different PHP installations on Windows by updating the PATH environment variable.

## Features

- Prompts for the root directory that contains PHP installations (e.g. `C:\php` with subfolders `php7.4`, `php8.2`, etc.).
- Offers to remember the chosen root directory in the `PHPSWITCHER_ROOT` user environment variable for future runs.
- Automatically filters out folders that do not contain a `php.exe` executable.
- Lists available PHP versions and lets you pick one interactively.
- Allows direct switching with a command such as `phpswitcher use php8.2`.
- Updates the user's PATH so the selected PHP version is placed first, removing other PHP folders from the same root.

## Usage

```bash
# Interactive mode – prompts for the root directory and lets you pick a PHP version
phpswitcher

# List all detected PHP installations under the provided root
phpswitcher list --root "C:\\php"

# Switch directly to a specific installation by name
phpswitcher use php8.2 --root "C:\\php"
```

You can also set an environment variable to avoid re-entering the root directory:

```powershell
setx PHPSWITCHER_ROOT "C:\\php"
```

When running interactively, the tool will offer to remember a newly entered root directory by setting the same environment variable for you.

> **Note:** Updating the PATH requires Windows. On other platforms the tool will only show which directory was selected.
