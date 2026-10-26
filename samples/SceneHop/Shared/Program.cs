// The only entry point of SceneHop. Every host compiles this exact file; each host assembly provides its own
// Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Samples.SceneHop;

return await GameApplication.RunAsync(new SceneHopGame(), PlatformBootstrap.Create(args));
