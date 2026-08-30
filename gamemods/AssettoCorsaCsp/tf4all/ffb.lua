-- TF4ALL FFB Bridge for Assetto Corsa (Custom Shaders Patch).
--
-- A CSP "ffb-postprocess" script: CSP calls script.update() once per
-- physics frame (333 Hz) with the game's post-processing-chain FFB value.
-- Normally a PURE PASS-THROUGH (it returns ffbValue and ffbDamper
-- unchanged): its job is publishing the game's FFB and per-wheel tire data
-- into a named shared memory section Trueforce For All reads from outside
-- the game. When TF4ALL is actively taking the wheel over (it says so
-- through a second, control shared-memory section, with a liveness
-- heartbeat), this script instead returns 0 to the wheel so the game stops
-- driving it, and TF4ALL renders the exported post-gain ffbValue itself.
-- That keeps the player's in-game gain AND every CSP FFB tweak intact
-- (ffbValue is post-tweak, post-gain) while freeing the wheel's HID pipe so
-- the rev lights and base screen do not fight the game's force. The moment
-- the heartbeat goes stale (SimHub closed), it reverts to pass-through, so
-- the wheel is never left silent.
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

-- TF4ALL -> script control channel. TF4ALL creates and writes this; we only
-- read it. seq advances by two per write and doubles as a liveness heartbeat.
-- We open it lazily (TF4ALL may start after AC) and fall back to pass-through
-- whenever it is missing, its header is wrong, or its writes go stale.
local CONTROL_LAYOUT = [[
  uint32_t magic;
  uint32_t version;
  uint32_t seq;
  uint32_t flags;
]]
local CONTROL_MAGIC = 0x54464331   -- "TFC1"
local ctrl = nil
local ctrlRetry = 0
local ctrlPrevSeq = -1
local ctrlSinceChange = 1e9        -- seconds since seq last advanced
local ctrlSuppress = false         -- last decoded suppress flag

local function readControl(dt)
  if ctrl == nil then
    ctrlRetry = ctrlRetry - dt
    if ctrlRetry > 0 then return false end
    ctrlRetry = 1.0
    pcall(function() ctrl = ac.readMemoryMappedFile('TF4All.ACBridge.Control.v1', CONTROL_LAYOUT) end)
    if ctrl == nil then return false end
  end
  local ok = false
  pcall(function()
    if ctrl.magic ~= CONTROL_MAGIC or ctrl.version ~= 1 then return end
    local seq = ctrl.seq
    if seq % 2 ~= 0 then                -- writer mid-write: keep the last decision
      ok = ctrlSuppress
      return
    end
    if seq ~= ctrlPrevSeq then
      ctrlPrevSeq = seq
      ctrlSinceChange = 0
    else
      ctrlSinceChange = ctrlSinceChange + dt
    end
    -- Stale writes = SimHub gone: revert to pass-through.
    if ctrlSinceChange > 0.5 then ok = false
    else ok = (ctrl.flags ~= 0) end
  end)
  ctrlSuppress = ok
  return ok
end

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

  -- If TF4ALL is taking the wheel over, hand it silence so the game stops
  -- driving the wheel; TF4ALL renders the exported ffbValue. Otherwise pass
  -- the game's own force through untouched.
  if readControl(dt) then
    return 0, 0
  end
  return ffbValue, ffbDamper
end
