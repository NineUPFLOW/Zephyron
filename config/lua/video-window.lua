-- Zephyron: малое окно приёма для узлов видео YouTube под фильтром — только тем адресам,
-- у которых ответ сервера уже срезали.
--
-- Фильтр у части групп серверов Google режет первый пакет ответа, если тот начинается
-- заголовком записи TLS (три байта). При окне приёма в один байт первый пакет несёт один
-- байт, фильтр его не узнаёт, дальше окно открывается само. Окно в один байт осмысленно
-- только без масштаба окна, поэтому в SYN опция масштаба убирается, и такое соединение
-- целиком живёт без него: не больше 64 КБ в пути (≈8 Мбит/с). Платить этим всем узлам
-- незачем: фильтр режет не каждое соединение. Поэтому окно — по адресу и на время.
--
-- Как узнаётся срезанный ответ: приветствие ушло, а клиент так и не подтвердил ни байта
-- ответа. Таймеров у функции нет, а зависшее соединение больше пакетов не шлёт, поэтому
-- вердикт выносится на следующем SYN к тому же адресу. Первый раз — окно на минуту,
-- второй раз подряд — на час. Удачное обычное соединение счёт сбрасывает.
--
-- Два места в строке winws2:
--   zy_video_window:syn — профиль по адресам Google с --filter-l7=unknown: SYN и
--     подтверждение рукопожатия (выносит вердикт, решает окно, убирает масштаб);
--   zy_video_window     — шаг в секциях googlevideo/youtube: ClientHello и дальше
--     (держит окно в один байт, пока не пришли два байта ответа; следит, был ли ответ).
-- Состояние соединения общее для профилей (track.lua_state), адресов — таблица ниже.
--
-- arg: syn     — шаг профиля SYN
-- arg: always  — окно всем соединениям профиля, без учёта истории адреса
-- arg: short=N — секунд окна после первого среза (60), long=N — после второго (3600)

local zy_cut = {}

local function zy_address(dis)
	local ip = dis_ipdst(dis)
	return ip and ntop(ip)
end

function zy_video_window(ctx, desync)
	local dis = desync.dis
	if not dis.tcp or not desync.outgoing or not desync.track then return end

	local state = desync.track.lua_state
	local now = os.time()

	if bitand(dis.tcp.th_flags, bitor(TH_SYN, TH_ACK)) == TH_SYN then
		if not desync.arg.syn then return end

		local address = zy_address(dis)
		if not address then return end

		local rec = zy_cut[address]
		if not rec then
			rec = { strikes = 0, till = 0 }
			zy_cut[address] = rec
		end

		-- Вердикт прошлому соединению: приветствие ушло давно, ответа не было.
		if rec.hello and not rec.replied and now - rec.hello >= 2 then
			rec.strikes = rec.strikes + 1
			rec.till = now + (rec.strikes >= 2 and tonumber(desync.arg.long or 3600) or tonumber(desync.arg.short or 60))
			rec.hello = nil
			DLOG("zy_video_window: "..address.." cut, window for "..(rec.till - now).." s")
		elseif rec.replied then
			rec.hello = nil
			rec.replied = nil
		end

		state.zy_address = address

		if desync.arg.always or now < rec.till then
			state.zy_window = true
			local i = find_tcp_option(dis.tcp.options, TCP_KIND_SCALE)
			if i then table.remove(dis.tcp.options, i) end
			return VERDICT_MODIFY
		end
		return
	end

	local address = state.zy_address
	if not address then return end
	local rec = zy_cut[address]
	if not rec then return end

	if not state.zy_ack0 then state.zy_ack0 = dis.tcp.th_ack end
	local got = u32add(dis.tcp.th_ack, -state.zy_ack0)

	if #dis.payload > 0 and not state.zy_hello then
		state.zy_hello = true
		rec.hello = now
		rec.replied = false
	end

	if got > 0 and state.zy_hello and not state.zy_replied then
		state.zy_replied = true
		rec.replied = true
		-- Обычное соединение прошло — счёт срезов с начала.
		if not state.zy_window then rec.strikes = 0 end
	end

	if state.zy_window and got < 2 then
		dis.tcp.th_win = 1
		return VERDICT_MODIFY
	end

	if state.zy_replied then instance_cutoff_shim(ctx, desync) end
end
