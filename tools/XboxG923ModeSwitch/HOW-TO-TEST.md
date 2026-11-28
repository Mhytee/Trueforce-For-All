# Xbox G923 PC-mode test

This is an experiment to switch the wheel into PC mode without G HUB. It may not work, but the result tells me what to try next. It doesn't install anything or change any drivers.

The test only works when the wheel starts in Xbox mode, so G HUB must not run at all while the wheel is plugged in until the tool tells you to open it.

1. Download and unzip XboxG923ModeSwitch.
2. Close SimHub.
3. Unplug the wheel (USB and power), then start your PC.
4. Make sure G HUB is fully closed: right-click its tray icon and choose **Quit**.
5. Plug the wheel back in (power and USB).
6. Run `XboxG923ModeSwitch.exe`. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
7. Read the RESULT line:
   - **SUCCESS:** just let me know. That's all I need.
   - **"already in PC mode":** the wheel started correctly this time. Try again another time.
   - **Anything else:** the tool asks whether to record what G HUB does. Type **C** and press Enter, then go to step 8.
8. When the tool says **"Now open G HUB"**, open G HUB and wait. Don't type anything while it records: the recording includes every USB device, your keyboard too. If Windows asks for permission, click **Yes**.
9. When it's done, it saves a file called `XboxG923-GHub-capture-<date>.zip` to your Desktop. Please send me that zip.
