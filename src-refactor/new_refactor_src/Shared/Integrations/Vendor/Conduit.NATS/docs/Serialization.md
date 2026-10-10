# Serialization (System.Text.Json + source generation)

Conduit.NATS serializes every NATS message with System.Text.Json. The built-in
`JsonSerializerRegistry` is configured to match FrostStream's HTTP JSON conventions:

- **camelCase** property names
- **NodaTime** via `ConfigureForNodaTime(DateTimeZoneProviders.Tzdb)`
- **string enums** via `JsonStringEnumConverter`
- nulls omitted on write

Out of the box it resolves type metadata by **reflection**. To make it **source-generated**
(AOT-friendly, faster startup, trim-safe) for your contracts, hand the library a
source-generated `JsonSerializerContext` — it stays in your app, because that's where the
concrete message types live.

## 1. Declare a context over your message contracts

```csharp
using System.Text.Json.Serialization;

namespace Shared.Messaging;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DownloadRequested))]
[JsonSerializable(typeof(MetadataFetched))]
[JsonSerializable(typeof(DownloadCompleted))]
[JsonSerializable(typeof(MovieQuery))]
[JsonSerializable(typeof(MovieQueryResult))]
// ...one [JsonSerializable] per message type crossing NATS
public partial class AppMessagesJsonContext : JsonSerializerContext;
```

## 2. Pass it via options

```csharp
builder.AddNats("nats", opts =>
{
    opts.JsonTypeInfoResolver = AppMessagesJsonContext.Default;
});
```

That's it. The library composes your context with a reflection fallback
(`JsonTypeInfoResolver.Combine(yourContext, new DefaultJsonTypeInfoResolver())`) and **still
layers on the NodaTime and string-enum converters**, so types your context covers go through
source-gen and anything it misses won't crash — it falls back to reflection.

> NodaTime types (`Instant`, `LocalDate`, …) used inside your contracts are handled by the
> NodaTime converters the library adds, so you do **not** need to register them in the context.

## 3. Strict AOT (no reflection fallback)

If you want zero reflection (e.g. full Native AOT), build the options yourself and assign them
verbatim — this takes precedence over `JsonTypeInfoResolver`:

```csharp
var json = JsonSerializerRegistry.CreateDefaultOptions(AppMessagesJsonContext.Default);
// Replace the combined resolver with ONLY the source-gen context:
json.TypeInfoResolver = AppMessagesJsonContext.Default;

builder.AddNats("nats", opts => opts.JsonSerializerOptions = json);
```

With this form the context must cover **every** type that crosses NATS; an uncovered type
throws at runtime instead of silently falling back.

## Why a CLR-type-per-version, not a wire envelope

Schema evolution uses **distinct CLR types** for breaking changes (e.g. `FooV1` → `FooV2`,
each on its own subject or with an `X-Schema-Version` header), rather than a positional binary
fingerprint. This keeps messages debuggable on the wire and consistent with the app's existing
JSON contracts.
