// The only entry point of the patrol sample (two NPC guards, a behavior tree and a state machine). Desktop, browser and headless hosts compile this exact
// file; each host assembly provides its own Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Samples.Patrol;

return await GameApplication.RunAsync(new PatrolGame(), PlatformBootstrap.Create(args));
