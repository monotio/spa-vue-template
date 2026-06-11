using System.Reflection;
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
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type)
                && !type.IsAbstract
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
            .Where(type => type.Namespace == ServicesNamespace
                && type.Name.EndsWith("Service", StringComparison.Ordinal));

        foreach (var type in serviceContracts)
        {
            var offendingMethods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && !IsServiceResponseShaped(method.ReturnType))
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
        var serviceTypes = _serverAssembly.GetTypes()
            .Where(type => type.Namespace == ServicesNamespace && type.IsPublic);

        foreach (var type in serviceTypes)
        {
            if (type.BaseType is not null)
            {
                CollectMvcTypes(type.BaseType, $"{type.Name} base type", violations);
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
