// The only entry point of Minimap. Desktop, browser and headless hosts compile this exact file; each host assembly
// provides its own Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Samples.Minimap;

return await GameApplication.RunAsync(new MinimapGame(), PlatformBootstrap.Create(args));
