global using System.Collections.Immutable;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Localization;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Options;
global using Aware.Models;
global using Aware.Presentation;
global using Aware.Services.Endpoints;
global using ApplicationExecutionState = Windows.ApplicationModel.Activation.ApplicationExecutionState;

// Aware's domain records are plain immutable values with full record equality.
// Uno.Extensions' implicit IKeyEquatable generation (KE0001) would key them by
// their Id property for reactive collections this app does not use.
[assembly: Uno.Extensions.Equality.ImplicitKeys(IsEnabled = false)]
