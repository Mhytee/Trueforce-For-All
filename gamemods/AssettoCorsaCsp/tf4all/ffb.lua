-- TF4ALL FFB Bridge for Assetto Corsa (Custom Shaders Patch).
--
-- A CSP "ffb-postprocess" script: CSP calls script.update() once per
-- physics frame (333 Hz) with the game's post-processing-chain FFB value.
-- This script is a PURE PASS-THROUGH (it returns ffbValue and ffbDamper
-- unchanged, so the game's force feedback is never altered); its only job
-- is publishing pre-gain FFB and per-wheel tire data into a named shared
-- memory section that Trueforce For All can read from outside the game.
--
-- Why: vanilla AC shared memory carries only finalFF (the post-gain output
-- signal). CSP additionally exposes steerTorque, ffbPure, ffbMultiplier and
-- per-wheel mz/fx/fy/load, none of which exist in the vanilla pages. Those
-- are the inputs for higher-fidelity AC effects work on the TF4ALL side.
--
-- Rates: the update() arguments and ac.getCarPhysicsRate() are true
-- physics-rate values. ac.getCar() state (ffbPure and friends) refreshes
-- once per GRAPHICS frame even when read from a physics script, so those
-- fields can trail by a few physics frames; consumers should treat them as
-- ~render-rate, which is fine for gain/shape decisions.
--
-- Seqlock: seq is bumped to ODD before the field writes and back to EVEN
-- after, so a reader that sees an odd seq (or a seq that changed across its
-- read) simply re-reads. Same convention CSP's own external-app bridges use.

local LAYOUT = [[
  uint32_t magic;
  uint32_t version;
  uint32_t seq;
  float ffbValue;
  float ffbPure;
  float ffbFinal;
  float ffbMultiplier;
  float steerTorque;
  float steerInput;
  float dt;
  float slipAngle[4];
  float slipRatio[4];
  float ndSlip[4];
  float load[4];
  float mz[4];
  float fx[4];
  float fy[4];
]]

local mmf = ac.writeMemoryMappedFile('TF4All.ACBridge.v1', LAYOUT)
mmf.magic   = 0x54463441   -- "TF4A" little-endian ("A4FT" as bytes)
mmf.version = 1
mmf.seq     = 0

-- Physics-rate per-wheel data needs a CSP new enough to expose
-- StateCarPhysicsRate.wheels (present in 0.2.11; absent in some older 0.2.x
-- builds). Feature-detect once; without it the per-wheel fields fall back to
-- the graphics-rate car.wheels values below.
local physRate = nil
pcall(function()
  local pr = ac.getCarPhysicsRate()
  if pr ~= nil and pr.wheels ~= nil then physRate = pr end
end)

function script.update(ffbValue, ffbDamper, steerInput, steerInputSpeed, dt)
  local s = mmf.seq + 1
  mmf.seq = s                      -- odd: writer busy

  mmf.ffbValue   = ffbValue
  mmf.steerInput = steerInput
  mmf.dt         = dt

  local car = ac.getCar(0)
  if car ~= nil then
    mmf.ffbPure       = car.ffbPure or 0
    mmf.ffbFinal      = car.ffbFinal or 0
    mmf.ffbMultiplier = car.ffbMultiplier or 1
    mmf.steerTorque   = car.steerTorque or 0
  end

  if physRate ~= nil then
    for i = 0, 3 do
      local w = physRate.wheels[i]
      mmf.slipAngle[i] = w.slipAngle    -- RADIANS on this struct
      mmf.slipRatio[i] = w.slipRatio
      mmf.ndSlip[i]    = w.ndSlip
      mmf.load[i]      = w.load
      mmf.mz[i]        = w.mz
      mmf.fx[i]        = w.fx
      mmf.fy[i]        = w.fy
    end
  elseif car ~= nil then
    for i = 0, 3 do
      local w = car.wheels[i]
      mmf.slipAngle[i] = math.rad(w.slipAngle)   -- DEGREES on StateWheel; normalize to radians
      mmf.slipRatio[i] = w.slipRatio
      mmf.ndSlip[i]    = w.ndSlip
      mmf.load[i]      = w.load
      mmf.mz[i]        = w.mz
      mmf.fx[i]        = w.fx
      mmf.fy[i]        = w.fy
    end
  end

  mmf.seq = s + 1                  -- even: stable
  return ffbValue, ffbDamper       -- never alter the game's FFB
end
