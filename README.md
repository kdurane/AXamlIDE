# AXamlIDE

## Project Overview
AXamlIDE is an advanced IDE built with Avalonia and .NET for XML/AXAML development. It provides intelligent code completion, real-time syntax checking, and a customizable editor interface.

## Key Features
- 🧠 Smart code completion with Dnlib metadata analysis
- 📌 Customizable margin indicators for code navigation
- 🛠️ Extensible editor features via plugin system
- 🌐 Cross-platform development support
- 🧪 Integrated diagnostic rendering and occurrence tracking

## Folder Structure
```
Assets/        - Theme configurations and icons
Controls/      - Custom UI components
EditorFeatures/ - Core editor functionality
Models/        - Data models and view models
Services/      - Backend services and utilities
Styles/        - Theming and styling definitions
Projects/      - Additional components:
                - `msbuild host` - For .NET project builds
                - `netcoredbg` - Third-party debugger (not included in repo)
```

## Additional Requirements
- **Roslyn LSP Server**: Install globally via `dotnet tool install --global roslyn-language-server --prerelease`

## Getting Started
1. Clone the repository
2. Restore NuGet packages via `dotnet restore`
3. Build the solution with `dotnet build`
4. Run the application using `dotnet run`

