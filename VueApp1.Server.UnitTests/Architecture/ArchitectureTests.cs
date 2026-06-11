using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using VueApp1.Server.Controllers;
using Xunit;

namespace VueApp1.Server.UnitTests.Architecture;

/// <summary>
/// The AGENTS.md "Architecture Rules" as executable tests, via plain
/// reflection (deliberately zero-dependency — no architecture-test package).
/// With these, <c>npm run check</c> itself rejects a controller that bypasses
/// <c>HandleServiceResponse</c> or a service that leaks HTTP shapes, instead
/// of relying on every contributor having read (and obeyed) the playbook.
/// </summary>
public class ArchitectureTests
{
    private const string ServicesNamespace = "VueApp1.Server.Services";

    private static readonly Assembly _serverAssembly = typeof(ApiControllerBase).Assembly;

    [Fact]
    public void Controllers_InheritApiControllerBase()
    {
        var violations = _serverAssembly.GetTypes()
            .Where(type => type.IsClass
                && !type.IsAbstract
                && !IsCompilerGenerated(type)
                && (typeof(ControllerBase).IsAssignableFrom(type) || IsDiscoverablePocoController(type))
                && !typeof(ApiControllerBase).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToList();

        if (violations.Count > 0)
        {
            Assert.Fail(
                "Controllers must inherit ApiControllerBase and return through "
                + "HandleServiceResponse (AGENTS.md, Architecture Rules: API layer). "
                + $"Violations: {string.Join(", ", violations)}");
        }
    }

    [Fact]
    public void ServiceContracts_ReturnServiceResponse()
    {
        // Scoped to *Service-named types: that suffix marks the
        // controller-facing service contract. Helper services with other
        // suffixes (e.g. UriLinkGenerator) may return domain types.
        var violations = new List<string>();
        var serviceContracts = _serverAssembly.GetTypes()
            .Where(type => IsInServicesNamespace(type)
                && !IsCompilerGenerated(type)
                && type.Name.EndsWith("Service", StringComparison.Ordinal));

        foreach (var type in serviceContracts)
        {
            var offendingMethods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => IsContractMethod(type, method) && !IsServiceResponseShaped(method.ReturnType))
                .Select(method => $"{type.Name}.{method.Name}");
            violations.AddRange(offendingMethods);
        }

        if (violations.Count > 0)
        {
            Assert.Fail(
                "Service contracts must return ServiceResponse/ServiceResponse<T> "
                + "(optionally Task- or ValueTask-wrapped) so controllers can route them "
                + "through HandleServiceResponse (AGENTS.md, Architecture Rules: API layer). "
                + $"Violations: {string.Join(", ", violations)}");
        }
    }

    [Fact]
    public void Services_DoNotExposeMvcTypes()
    {
        // Reflection sees declared signatures, not method bodies — good
        // enough to catch the real failure mode: a service returning
        // IActionResult/ProblemDetails instead of a ServiceResponse.
        var violations = new List<string>();
        // Internal types are deliberately in scope — layering rules do not
        // weaken with the access modifier; only compiler-generated noise
        // (closures, state machines) is excluded.
        var serviceTypes = _serverAssembly.GetTypes()
            .Where(type => IsInServicesNamespace(type) && !IsCompilerGenerated(type));

        foreach (var type in serviceTypes)
        {
            if (type.BaseType is not null)
            {
                CollectMvcTypes(type.BaseType, $"{type.Name} base type", violations);
            }

            foreach (var implementedInterface in type.GetInterfaces())
            {
                CollectMvcTypes(implementedInterface, $"{type.Name} implements", violations);
            }

            foreach (var field in type.GetFields(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                CollectMvcTypes(field.FieldType, $"{type.Name}.{field.Name}", violations);
            }

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                CollectMvcTypes(method.ReturnType, $"{type.Name}.{method.Name} return type", violations);
                foreach (var parameter in method.GetParameters())
                {
                    CollectMvcTypes(
                        parameter.ParameterType, $"{type.Name}.{method.Name} parameter '{parameter.Name}'", violations);
                }
            }

            foreach (var property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                CollectMvcTypes(property.PropertyType, $"{type.Name}.{property.Name}", violations);
            }

            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    CollectMvcTypes(
                        parameter.ParameterType, $"{type.Name} constructor parameter '{parameter.Name}'", violations);
                }
            }
        }

        if (violations.Count > 0)
        {
            Assert.Fail(
                "Services must not expose Microsoft.AspNetCore.Mvc types — HTTP shaping "
                + "belongs in controllers via HandleServiceResponse; services return "
                + "ServiceResponse (AGENTS.md, Architecture Rules: API layer). "
                + $"Violations: {string.Join(", ", violations)}");
        }
    }

    private static readonly Type[] _disposableInterfaces = [typeof(IDisposable), typeof(IAsyncDisposable)];

    private static bool IsInServicesNamespace(Type type) =>
        type.Namespace == ServicesNamespace
        || type.Namespace?.StartsWith(ServicesNamespace + ".", StringComparison.Ordinal) == true;

    private static bool IsCompilerGenerated(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || type.Name.Contains('<', StringComparison.Ordinal);

    /// <summary>
    /// Mirrors MVC's <c>ControllerFeatureProvider.IsController</c>: ASP.NET
    /// Core also discovers POCO controllers (public class with the
    /// "Controller" suffix or [Controller]/[ApiController]) that never derive
    /// from ControllerBase — exactly the shape that would otherwise serve
    /// routes while bypassing HandleServiceResponse unseen.
    /// </summary>
    private static bool IsDiscoverablePocoController(Type type) =>
        type.IsPublic
        && !type.ContainsGenericParameters
        && !type.IsDefined(typeof(NonControllerAttribute), inherit: true)
        && (type.Name.EndsWith("Controller", StringComparison.Ordinal)
            || type.IsDefined(typeof(ControllerAttribute), inherit: true));

    /// <summary>
    /// Object overrides (ToString/Equals/GetHashCode) and the dispose pattern
    /// are legitimate non-contract members; only genuine service operations
    /// must be ServiceResponse-shaped.
    /// </summary>
    private static bool IsContractMethod(Type type, MethodInfo method)
    {
        if (method.IsSpecialName)
        {
            return false;
        }

        if (method.GetBaseDefinition().DeclaringType == typeof(object))
        {
            return false;
        }

        return !ImplementsDisposeMethod(type, method);
    }

    private static bool ImplementsDisposeMethod(Type type, MethodInfo method)
    {
        if (type.IsInterface)
        {
            // An interface inheriting IDisposable/IAsyncDisposable does not
            // re-declare Dispose, so DeclaredOnly never surfaces it here.
            return false;
        }

        foreach (var disposableInterface in _disposableInterfaces)
        {
            if (disposableInterface.IsAssignableFrom(type)
                && type.GetInterfaceMap(disposableInterface).TargetMethods.Contains(method))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsServiceResponseShaped(Type returnType)
    {
        var unwrapped = returnType;
        if (unwrapped.IsGenericType
            && (unwrapped.GetGenericTypeDefinition() == typeof(Task<>)
                || unwrapped.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            unwrapped = unwrapped.GetGenericArguments()[0];
        }

        return typeof(ServiceResponse).IsAssignableFrom(unwrapped);
    }

    private static void CollectMvcTypes(Type type, string location, List<string> violations)
    {
        if (type.Namespace?.StartsWith("Microsoft.AspNetCore.Mvc", StringComparison.Ordinal) == true)
        {
            violations.Add($"{location} uses {type.Name}");
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                CollectMvcTypes(argument, location, violations);
            }
        }
    }
}
