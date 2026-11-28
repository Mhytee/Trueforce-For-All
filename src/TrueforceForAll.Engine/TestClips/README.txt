Test-button clips
=================

Each .csv here is a few seconds of recorded telemetry that an effect's Test
button replays through the effect's real OnTelemetry (EngineLoop.StartClipTest,
EffectClipTest). The file name is the effect's class name, and that is the whole
registration: drop in SomeEffect.csv and SomeEffect's Test button uses it. The
clips are embedded in TrueforceForAll.Engine.dll. An effect with no clip keeps
its synthetic TestPlay.

Cutting a clip
--------------

1. Record: type the CAPTURE access code, drive the moment, type it again. The
   file lands in Documents\TrueforceForAll.
2. Find the window:
     dotnet run --project tools/FixturePrep -- info capture.csv
3. Cut it, keeping only the columns the effect reads (t_us is always kept, and
   a per-tire quad is named without its _fl/_fr/_rl/_rr suffix):
     dotnet run --project tools/FixturePrep -- clip capture.csv src/TrueforceForAll.Engine/TestClips/SomeEffect.csv 12.5 15.0 --keep speed_kmh,rpm,max_rpm,throttle,gear,wheel_slip
4. Check that the effect speaks, and when:
     dotnet run --project tools/FixturePrep -- audition src/TrueforceForAll.Engine/TestClips/SomeEffect.csv SomeEffect
   Add Property=value to try a slider (Sensitivity=0.18), and --realtime for an
   effect that times a hold on the wall clock (the redline buzz).
5. dotnet test src/TrueforceForAll.Core.Tests. EffectClipTestTests plays every
   clip here and fails one that leaves its effect silent.

What makes a good clip
----------------------

- One clear moment, 1 to 4.5 seconds. The button should answer at once: start
  the window no more than about 150 ms before the effect first speaks.
- A strong version of the event. The user's thresholds are live during the
  test, so a marginal slide plays as silence for anyone running a low
  sensitivity.
- Only the columns the effect reads. Never num_cyl: the engine layout is the
  user's, and the player strips it anyway.
- Revs are replayed relative to the clip's max_rpm and mapped onto the live
  car's when one is known, so keep max_rpm in any clip that carries rpm.

The clips
---------

All eight come from the two Forza Motorsport parity fixtures in
src/TrueforceForAll.Core.Tests/Fixtures, with the same base columns
(speed_kmh,rpm,max_rpm,throttle,gear) plus the ones listed.

  EnginePulseEffect   fm8-limit-wall  3.3-7.6     (base only)
      Full-throttle pull through 3rd and 4th: revs climb, drop on the shift,
      climb again.
  GearShiftEffect     fm8-limit-wall  13.9-15.75  (base only)
      Three quick downshifts into a corner, then the upshift out of it.
  RevLimiterEffect    fm8-limit-wall  28.2-31.5   (base only)
      Wheelspin launch held against the limiter through 1st, 2nd and 3rd.
  TractionLossEffect  fm8-mixed       4.7-7.2     + wheel_slip
      Rear steps out under power, a big slide, then it grips again.
  AxleSlipEffect      fm8-limit-wall  14.8-18.0   + accel_sway, slip_ratio,
                                                    combined, susp_m, wheel_rot
      Power oversteer out of a corner: rear first, then both axles working.
  KerbThumpEffect     fm8-limit-wall  18.1-19.0   + on_strip
      Four wheel contacts along a curb at 145 km/h.
  LockupJudderEffect  fm8-limit-wall  10.8-14.1   + slip_ratio
      Fronts locked from 255 km/h down to 80.
  RoadBumpsEffect     fm8-limit-wall  19.5-21.8   + accel_heave, surface_rumble
      Rough curb surface, then the jolt of dropping off it. The surface
      texture half is Forza's channel. The player passes it through and the
      effect's own "Surface texture" tick box decides whether it plays, as on
      track: unticked, the test is the bumps alone (owner call 2026-11-11).
      Planned: a second clip with another game's surface texture, offered as
      a choice.

Those fixtures were recorded before the Forza source learned this title's gear
numbering, so their gear column reads one low (1st as N) and shows the neutral
each shift passes through as 10. Every clip above was cut with
  --gears N=1,1=2,2=3,3=4,4=5,10=hold
which relabels it to what the source emits today. A new recording needs no
--gears.

Still on the synthetic test
---------------------------

No recording carries these signals yet. Each needs one capture:

  AbsClickEffect      hard braking with ABS cycling (Assetto Corsa reports the
                      real pump cycle)
  CollisionEffect     a wall or car contact above 5 g
  PitLimiterEffect    a pit lane pass on the limiter
  DrsEffect           a DRS zone, open to close
  ImplementThudEffect Farming Simulator, an implement lowered: the hum while
                      it travels and the thud when it settles. info lists
                      every motion start, settle, flap and stage end with its
                      time. End the window after the settle thud, and keep
                        --keep impl_working,impl_moving,impl_speed,impl_event,impl_phase,impl_phase_speed,impl_manual
                      The counters are per session; the player makes the
                      switch into the clip and back silent (OnClipBoundary).
