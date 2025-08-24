# Gelatinarm Documentation

Welcome to the Gelatinarm project documentation. This documentation provides comprehensive information about the Xbox Jellyfin client application.

## Start Here

**First-time setup?** → Read **[DEV_SETUP.md](DEV_SETUP.md)** for environment setup and build instructions

## Documentation Structure

### Core Documentation
- **[PROJECT_DOCUMENTATION.md](PROJECT_DOCUMENTATION.md)** - Complete project overview, file reference, and development guidelines
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Architecture patterns, design decisions, and coding standards
- **[NAVIGATION_FLOW.md](NAVIGATION_FLOW.md)** - Visual navigation maps and flow diagrams
- **[CONTROLLER_ARCHITECTURE.md](CONTROLLER_ARCHITECTURE.md)** - Xbox controller input system documentation
- **[CODING_PATTERNS.md](CODING_PATTERNS.md)** - Established coding patterns after consolidation

### Developer Guides
- **[DEV_SETUP.md](DEV_SETUP.md)** - Development environment setup and build instructions
- **[COMMON_TASKS.md](COMMON_TASKS.md)** - Implementation examples and patterns

## Quick Links

### For Developers
1. Start with [DEV_SETUP.md](DEV_SETUP.md) to set up your development environment
2. Review [ARCHITECTURE.md](ARCHITECTURE.md) to understand the project structure
3. Check [COMMON_TASKS.md](COMMON_TASKS.md) for implementation examples

### For Feature Development
1. Consult [PROJECT_DOCUMENTATION.md](PROJECT_DOCUMENTATION.md) for file locations
2. Follow patterns in [CODING_PATTERNS.md](CODING_PATTERNS.md) and [COMMON_TASKS.md](COMMON_TASKS.md)
3. Use [NAVIGATION_FLOW.md](NAVIGATION_FLOW.md) to understand page relationships
4. Review [CONTROLLER_ARCHITECTURE.md](CONTROLLER_ARCHITECTURE.md) for Xbox input handling

### For Maintenance
1. Reference [PROJECT_DOCUMENTATION.md](PROJECT_DOCUMENTATION.md) for service descriptions
2. Check [ARCHITECTURE.md](ARCHITECTURE.md) for design patterns
3. Review error handling patterns in [COMMON_TASKS.md](COMMON_TASKS.md)

## Key Concepts Summary

### Technology Stack
- **Platform**: Universal Windows Platform (UWP)
- **Target**: Xbox One, Xbox Series S/X
- **Language**: C# with XAML
- **Pattern**: MVVM with Services
- **Server**: Jellyfin Media Server

### Project Structure
```
Gelatinarm/
├── Views/          # User interface pages
├── ViewModels/     # Business logic for views
├── Services/       # Core functionality
├── Models/         # Data structures
├── Controls/       # Reusable UI components
├── Helpers/        # Utility functions
└── Docs/           # Project documentation
```

### Development Workflow
1. **UI Changes**: Modify XAML in Views or Controls
2. **Logic Changes**: Update ViewModels or Services
3. **Features**: Add View + ViewModel + Service (if required)
4. **Testing**: Run on Windows, then deploy to Xbox

## Getting Help

### Finding Information
- **File Locations**: See "File Directory Reference" in PROJECT_DOCUMENTATION.md
- **Navigation Flow**: See visual diagrams in NAVIGATION_FLOW.md
- **Code Examples**: See patterns in COMMON_TASKS.md
- **Architecture Questions**: See ARCHITECTURE.md

### Common Issues
- **Build Problems**: Check prerequisites in DEV_SETUP.md
- **Navigation Issues**: Verify flow in NAVIGATION_FLOW.md
- **Service Errors**: Check service descriptions in PROJECT_DOCUMENTATION.md

## Contributing

When contributing to the project:
1. Follow the coding standards in ARCHITECTURE.md
2. Use existing patterns shown in COMMON_TASKS.md
3. Update documentation if adding new features
4. Test on Xbox hardware before submitting changes

## Documentation Standards

Documentation requirements:
- **Accurate**: Reflect actual code implementation
- **Factual**: Use objective, timeless language
- **Complete**: Cover all major components
- **Practical**: Include working examples from the codebase