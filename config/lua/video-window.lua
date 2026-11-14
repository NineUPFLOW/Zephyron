-- Zephyron: малое окно приёма для узлов видео YouTube под фильтром — только группам узлов,
-- у которых ответ сервера уже срезали.
--
-- Фильтр у части групп серверов Google режет первый пакет ответа, если тот начинается
-- заголовком записи TLS (три байта). При окне приёма в один байт первый пакет несёт один
-- байт, фильтр его не узнаёт, дальше окно открывается само. Окно в один байт осмысленно
-- только без масштаба окна, поэтому в SYN опция масштаба убирается, и такое соединение
-- целиком живёт без него: не больше 64 КБ в пути (≈8–16 Мбит/с). Платить этим всем узлам
-- незачем: фильтр режет не каждое соединение. Поэтому окно — по группе и на время.
--
-- Группа — 16 соседних адресов (последний байт без четырёх младших бит): узлы rr1…rr5
-- одной площадки стоят подряд, а соседние шестнадцатки бывают разными площадками.
--
-- Срез узнаётся так: приветствие ушло, а подтверждения ответа за after секунд нет.
--   * Таймер на приветствии через resetafter мс шлёт браузеру RST от имени сервера:
--     плеер сразу открывает новое соединение, а группа к этому мигу уже помечена,
--     и повтор идёт с малым окном. Без сброса первое срезанное соединение висело
--     до таймаута браузера.
--   * Если таймер не успел (соединение закрыли раньше), вердикт выносится на
--     следующем SYN к группе.
--   Срезы помнятся по соединениям: плеер открывает к узлу несколько сразу, и ответ
--   одного не стирает срез другого. Ответ засчитывается, только если пришёл в срок
--   и с флагом ACK: сервер повторяет ответ и после того, как клиент сдался.
--
-- Сколько держать окно: срез — минута. Группе, которая обычным соединением ни разу
-- не ответила, второй срез подряд — час (узел закрыт наглухо); отвечавшей — час
-- только с третьего. Далёкой группе (наименьшая задержка SYN→приветствие больше far мс)
-- первый срез подряд прощается: окно там режет скорость до уровня ниже 1440p.
-- Удачное обычное соединение счёт сбрасывает.
--
-- Два места в строке winws2:
--   zy_video_window:syn — профиль по адресам Google с --filter-l7=unknown: SYN и
--     подтверждение рукопожатия (вердикт по прошлым соединениям, решение об окне);
--   zy_video_window     — шаг в секциях googlevideo/youtube: ClientHello и дальше
--     (окно в один байт, пока не пришли два байта ответа; учёт ответа; таймер сброса).
-- Состояние соединения общее для профилей (track.lua_state), групп — таблица ниже.
--
-- arg: syn          — шаг профиля SYN
-- arg: always       — окно всем соединениям профиля, без учёта истории группы
-- arg: short=N      — секунд окна после среза (60); long=N — «час» (3600)
-- arg: after=N      — секунд на ответ после приветствия (2)
-- arg: resetafter=N — мс до сброса молчащего соединения (1000); 0 — без сброса
-- arg: far=N        — мс задержки, после которой первый срез прощается (35)

local zy_groups = {}
local zy_next_id = 0

local function zy_group_key(dis)
	local ip = dis_ipdst(dis)
	if not ip then return nil end
	local text = ntop(ip)
	local a, b, c, d = text:match("^(%d+)%.(%d+)%.(%d+)%.(%d+)$")
	if a then
		return a.."."..b.."."..c.."."..(tonumber(d) - tonumber(d) % 16)
	end
	return text
end

local function zy_group(key)
	local g = zy_groups[key]
	if not g then
		g = { strikes = 0, till = 0, answered = false, rtt = nil, pending = {} }
		zy_groups[key] = g
	end
	return g
end

-- Срез одного соединения группы: счёт и срок окна.
local function zy_cut(g, key, arg, now)
	g.strikes = g.strikes + 1

	local far = tonumber(arg.far or 35) / 1000
	if g.strikes == 1 and g.rtt and g.rtt > far then
		DLOG("zy_video_window: "..key.." far group ("..math.floor(g.rtt * 1000).." ms), first cut forgiven")
		return
	end

	local long = g.strikes >= (g.answered and 3 or 2)
	g.till = now + (long and tonumber(arg.long or 3600) or tonumber(arg.short or 60))
	DLOG("zy_video_window: "..key.." cut #"..g.strikes..", window for "..math.floor(g.till - now).." s")
end

-- Таймер на приветствии: ответа нет — срез, и браузеру RST от имени сервера.
function zy_video_window_timer(name, data)
	local state = data.state
	if state.zy_replied then return end

	local g = zy_groups[state.zy_group]
	if g and g.pending[state.zy_id] then
		g.pending[state.zy_id] = nil
		zy_cut(g, state.zy_group, data.arg, clock_getfloattime())
	end

	local dis = data.dis
	local sent = #dis.payload
	dis.payload = nil
	dis_reverse(dis)
	-- RST с подтверждением всего приветствия: так его принимает и стек с поднятым TUN.
	dis.tcp.th_ack = u32add(dis.tcp.th_ack, sent)
	dis.tcp.th_flags = bitor(TH_RST, TH_ACK)
	dis.tcp.options = nil
	DLOG("zy_video_window: "..state.zy_group.." no answer, RST to the client")
	-- Интерфейс исходного пакета: без него WinDivert при поднятом TUN кладёт сброс не туда (13.11).
	rawsend_dissect(dis, { ifout = data.ifin })
end

function zy_video_window(ctx, desync)
	local dis = desync.dis
	if not dis.tcp or not desync.outgoing or not desync.track then return end

	local state = desync.track.lua_state
	local arg = desync.arg
	local now = clock_getfloattime()

	if bitand(dis.tcp.th_flags, bitor(TH_SYN, TH_ACK)) == TH_SYN then
		if not arg.syn then return end

		local key = zy_group_key(dis)
		if not key then return end
		local g = zy_group(key)

		-- Вердикт прошлым соединениям группы, чей таймер не успел.
		local after = tonumber(arg.after or 2)
		for id, hello in pairs(g.pending) do
			if now - hello >= after then
				g.pending[id] = nil
				zy_cut(g, key, arg, now)
			end
		end

		if not state.zy_id then
			zy_next_id = zy_next_id + 1
			state.zy_id = zy_next_id
		end
		state.zy_group = key
		state.zy_syn = now

		if arg.always or now < g.till then
			state.zy_window = true
			local i = find_tcp_option(dis.tcp.options, TCP_KIND_SCALE)
			if i then table.remove(dis.tcp.options, i) end
			return VERDICT_MODIFY
		end
		return
	end

	local key = state.zy_group
	if not key then return end
	local g = zy_groups[key]
	if not g then return end

	if not state.zy_ack0 then state.zy_ack0 = dis.tcp.th_ack end
	local got = u32add(dis.tcp.th_ack, -state.zy_ack0)

	if #dis.payload > 0 and not state.zy_hello then
		state.zy_hello = now

		-- Считаем и сбрасываем только узлы видео: фронты youtube.com и другие службы Google
		-- живут в тех же сетях, и обрывать их или учить их молчанием группу незачем.
		local host = desync.track.hostname
		if not host or not (host:match("^rr%d+%-%-%-sn%-[%w%-]+%.googlevideo%.com$")
				or host:match("^rr%d+%-%-%-sn%-[%w%-]+%.c%.youtube%.com$")) then
			state.zy_untracked = true
		end
	end

	if state.zy_hello and not state.zy_untracked and not state.zy_started then
		state.zy_started = true
		g.pending[state.zy_id] = now

		if state.zy_syn then
			local rtt = now - state.zy_syn
			if not g.rtt or rtt < g.rtt then g.rtt = rtt end
		end

		local resetafter = tonumber(arg.resetafter or 1000)
		if resetafter > 0 then
			timer_set("zy_video_window_"..state.zy_id, "zy_video_window_timer", resetafter, true,
				{ state = state, arg = arg, dis = deepcopy(dis), ifin = (desync.ifout and #desync.ifout > 0) and desync.ifout or desync.ifin })
		end
	end

	if got > 0 and state.zy_hello and not state.zy_replied and not state.zy_untracked
		and bitand(dis.tcp.th_flags, TH_ACK) ~= 0
		and now - state.zy_hello <= tonumber(arg.after or 2) then
		state.zy_replied = true
		g.pending[state.zy_id] = nil
		-- Обычное соединение прошло — группа отвечает, счёт срезов с начала.
		if not state.zy_window then
			g.strikes = 0
			g.answered = true
		end
	end

	if state.zy_window and got < 2 then
		dis.tcp.th_win = 1
		return VERDICT_MODIFY
	end

	if state.zy_replied then instance_cutoff_shim(ctx, desync) end
end
