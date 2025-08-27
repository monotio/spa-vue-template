# Vue 3 + .NET 9 SPA Template

A modern single-page application template with Vue 3 frontend and .NET 9 backend.

## Features

- **Frontend**: Vue 3 with Composition API (script setup)
- **Backend**: .NET 9 with C# 12 features
- **Build Tools**: Vite 7 with HMR
- **TypeScript**: Full TypeScript support with strict mode
- **Modern Syntax**: Records, primary constructors, collection expressions
- **API**: RESTful API with OpenAPI support
- **Development**: Vue DevTools integration

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 20.19+ or 22.12+](https://nodejs.org/)

## Getting Started

### Install dependencies
```bash
cd vueapp1.client
npm install
```

### Development

Run both frontend and backend:
```bash
# Terminal 1: Start .NET backend
cd VueApp1.Server
dotnet run

# Terminal 2: Start Vue dev server
cd vueapp1.client
npm run dev
```

Or use Visual Studio / VS Code launch configurations.

### Build for Production

```bash
# Build Vue app
cd vueapp1.client
npm run build

# Build .NET app
cd VueApp1.Server
dotnet publish -c Release
```

### Run Unit Tests

```sh
npm run test
# or with UI
npm run test:ui
```

## Project Structure

```
├── VueApp1.Server/           # .NET 9 Backend
│   ├── Controllers/          # API Controllers
│   ├── Program.cs            # Application entry point
│   └── appsettings.json      # Configuration
│
└── vueapp1.client/           # Vue 3 Frontend
    ├── src/
    │   ├── components/       # Vue components
    │   ├── assets/           # Static assets
    │   └── main.ts           # App entry point
    ├── vite.config.ts        # Vite configuration
    └── tsconfig.json         # TypeScript configuration
```

## Available Scripts

### Frontend
- `npm run dev` - Start dev server with HMR
- `npm run build` - Build for production
- `npm run preview` - Preview production build
- `npm run type-check` - Run TypeScript type checking
- `npm run lint` - Lint and fix code

### Backend
- `dotnet run` - Run development server
- `dotnet build` - Build the project
- `dotnet publish` - Publish for deployment
- `dotnet test` - Run tests

## API Documentation

When running in development, OpenAPI documentation is available at:
- https://localhost:7191/openapi/v1.json

## License

MIT