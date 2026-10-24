// The only entry point of the camera rig. Desktop, browser and headless hosts compile this exact
// file; each host assembly provides its own Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Samples.CameraRig;

return await GameApplication.RunAsync(new CameraRigGame(), PlatformBootstrap.Create(args));
