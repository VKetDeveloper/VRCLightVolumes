[VRC Light Volumes](../README.md) | **How to Use** | [Best Practices](../Documentation/BestPractices.md) | [Udon Sharp API](../Documentation/UdonSharpAPI.md) | [For Shader Developers](../Documentation/ForShaderDevelopers.md) | [Compatible Shaders](../Documentation/CompatibleShaders.md)

# How to Use

> [VRC Light Volumes System](../Documentation/HowToUse.md)
>
> [Regular Light Volumes](../Documentation/HowToUse_RegularLightVolumes.md)
>
> [Point Light Volumes](../Documentation/HowToUse_PointLightVolumes.md)
>
> **Audio Link Integration**
>
> - [Audio Link Quick Setup](#Audio-Link-Quick-Setup)
> - [Light Volume Audio Link Component Description](#Light-Volume-Audio-Link-Component-Description)
>
> [TV Screens Integration](../Documentation/HowToUse_TVScreensIntegration.md)

## Audio Link Integration

This package includes a [AudioLink](https://github.com/llealloo/audiolink/) integration Udon script. 

![](../Documentation/Preview_14.gif)

**LightVolumeAudioLink** component can change Light Volumes, Point light Volumes and Mesh Renderers materials colors in runtime based on AudioLink.

## Audio Link Quick Setup

1. Add the **LightVolumeAudioLink** component to a GameObject in your scene.

2. One **LightVolumeAudioLink** can control Light Volumes, Point Light Volumes and material colors based on an `Audio Band` value you choose: `Bass`, `Low Mid`, `High Mid`, `Treble`

3. Assign your **Audio Link** object in the `Audio Link` field.

4. Add all Light Volumes you want to control to the `Target Light Volumes` list and Point Light Volumes in the `Target Point Light Volumes` list.

5. Add all Mesh Renderers you want to change emission color by AudioLink to the `Target Mesh Renderers` list.

   > ⚠️ Note: These meshes should use materials with **emission enabled**. The shader must include a property named `_EmissionColor` (the **Standard** shader supports this).

6. Adjust `Materials Intensity` to fine-tune the brightness of your materials. `Intensity` of Light Volumes and Point Light Volumes can be configured in their components.

7. In your **AudioLink** component, make sure that **GPU Readback** is enabled. Click `Enable readback` if it’s not already active.

8. Enable `Auto Update Volumes` in your **Light Volume Setup** to let it update the colors automatically in runtime.

9. Done! You should now see visual changes reacting to audio.
   Add more **LightVolumeAudioLink** components to control other AudioLink bands.

## Light Volume Audio Link Component Description

`Audio Link` - Reference to your Audio Link Manager that should control Light Volumes.

`Audio Band` - Defines which audio band will be used to control Light Volumes. Four bands available: **Bass, Low Mid, High Mid, Treble**.

`Delay` - Defines how many samples back in history we're getting data from. Can be a value from **0** to **127**. Zero means no delay at all.

`Smoothing Enabled` - Enables smoothing algorithm that tries to smooth out flickering that can usually be a problem.

`Smoothing` - Value from 0 to 1 that defines how much smoothing should be applied. **Zero** usually applies just a little bit of smoothing. **One** smoothes out almost all the fast blinks and makes intensity changing very slow.

`ColorMode` - Auto uses Theme Colors 0, 1, 2, 3 for Bass, LowMid, HighMid, Treble. Override Color allows you to set the static color value. 

`Color` - Color that will be used when **Override Color** is enabled.

`Target Light Volumes` - List of the **Light Volumes** that should be affected by AudioLink.

`Target Point Light Volumes` - List of the **Point Light Volumes** that should be affected by AudioLink.

`Target Mesh Renderers` - List of the **Mesh Renderers** that has materials that should change color based on AudioLink.

`Materials Intensity` - Brightness multiplier of the materials that should change color based on AudioLink.