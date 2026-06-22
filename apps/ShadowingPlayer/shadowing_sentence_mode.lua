local utils = require "mp.utils"

local segments = {}
local enabled = false
local current_index = 1
local seek_grace_until = 0

local timer

timer = mp.add_periodic_timer(0.02, function()
    if not enabled then
        return
    end

    local segment = segments[current_index]
    if not segment then
        return
    end

    if mp.get_time() < seek_grace_until then
        return
    end

    local position = mp.get_property_number("time-pos")
    if not position then
        return
    end

    if position >= segment["end"] then
        mp.set_property_bool("pause", true)
        timer:stop()  -- FIX 1: was timer:kill() which permanently destroys the timer.
                      -- stop() pauses it so timer:resume() works on the next sentence.
    end
end, true)

local function clamp_index(index)
    if #segments == 0 then
        return 1
    end

    index = tonumber(index) or 1
    if index < 1 then
        return 1
    end

    if index > #segments then
        return #segments
    end

    return index
end

local function find_sentence_at(position)
    if #segments == 0 then
        return 1
    end

    position = tonumber(position) or 0

    for index, segment in ipairs(segments) do
        if position >= segment["start"] and position < segment["end"] then
            return index
        end

        if position < segment["start"] then
            return index
        end
    end

    return #segments
end

local function start_timer()
    if #segments == 0 then
        return
    end

    timer:resume()
end

local function play_index(index)
    if #segments == 0 then
        return
    end

    current_index = clamp_index(index)
    local segment = segments[current_index]

    -- FIX 3: write current index into user-data so C# can read it back
    -- through the existing pipe without opening a second connection.
    -- Value is 0-based to match C# indexing convention.
    mp.set_property("user-data/shadow/index", tostring(current_index - 1))

    seek_grace_until = mp.get_time() + 0.7
    enabled = true
    mp.commandv("seek", tostring(segment["start"]), "absolute", "exact")
    mp.set_property_bool("pause", false)
    start_timer()
end

mp.register_script_message("load", function(json)
    local parsed = utils.parse_json(json or "")
    if type(parsed) ~= "table" then
        segments = {}
        current_index = 1
        enabled = false
        timer:stop()  -- FIX 1: was timer:kill()
        return
    end

    segments = parsed
    current_index = clamp_index(current_index)
    enabled = false
    timer:stop()  -- FIX 1: was timer:kill()
end)

mp.register_script_message("enable", function()
    if #segments == 0 then
        return
    end

    local position = mp.get_property_number("time-pos", 0)
    current_index = find_sentence_at(position)
    seek_grace_until = 0
    enabled = true
    mp.set_property_bool("pause", false)
    start_timer()
end)

mp.register_script_message("disable", function()
    enabled = false
    timer:stop()  -- FIX 1: was timer:kill()
end)

mp.register_script_message("play", function(index)
    play_index(index)
end)

mp.register_script_message("repeat", function()
    play_index(current_index)
end)

mp.register_script_message("previous", function()
    play_index(current_index - 1)
end)

mp.register_script_message("next", function()
    play_index(current_index + 1)
end)
