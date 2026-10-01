// The only entry point of LanternRun. Desktop, browser and headless hosts compile this exact file; each host
// assembly provides its own Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Samples.LanternRun;

return await GameApplication.RunAsync(new LanternRunGame(), PlatformBootstrap.Create(args));
