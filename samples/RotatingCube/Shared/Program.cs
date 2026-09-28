// The only entry point of the rotating cube. Desktop, browser and headless hosts compile this exact
// file; each host assembly provides its own Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Samples.RotatingCube;

return await GameApplication.RunAsync(new CubeGame(), PlatformBootstrap.Create(args));
