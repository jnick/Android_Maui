// Подключаем базовые классы MVVM (ObservableObject, атрибут ObservableProperty)
using CommunityToolkit.Mvvm.ComponentModel;
// Подключаем модели распознавания: снимок кадра
using MauiBluethooth.Models.Detection;
// Подключаем интерфейс Bluetooth-сервиса
using MauiBluethooth.Services;
// Подключаем System.Diagnostics для вывода в лог отладки
using System.Diagnostics;

// Объявляем пространство имён передачи результатов по Bluetooth
namespace MauiBluethooth.Services.Detection.Transmission;

// Передатчик: по расписанию забирает последний снимок распознавания
// и отправляет его подключённому Bluetooth-устройству в виде пакетов S/D/E.
//
// Роль в архитектуре:
//   • распознавание живёт в ObjectDetector и сообщает о результатах событием SnapshotReady;
//   • передатчик — аустентичный подписчик этого события: он помнит «последний снимок»
//     и по таймеру (5 Гц) выталкивает его наружу;
//   • интерфейс (CameraViewModel) только включает флаг IsTransmissionEnabled,
//     а вся логика отправки — здесь.
//
// Почему таймер, а не отправка на каждый снимок. Распознавание выдаёт кадры
// несколько раз в секунду, но устройству-приёмнику (Arduino) не нужно такое
// количество обновлений — ему достаточно 5 раз в секунду. Привязываться к кадрам
// было бы неверно ещё и потому, что частота кадров зависит от выбранной модели,
// и протокол получился бы нестабильным
public sealed partial class DetectionTransmitter : ObservableObject, IDisposable
{
	// Период отправки в миллисекундах: 200 мс = 5 кадров (обновлений) в секунду.
	// Значение согласовано с возможностями HC-05 (линия 9600 бод и маленький буфер)
	private const int SendPeriodMilliseconds = 200;

	// Пауза между записями одной и той же ошибки в лог (в миллисекундах).
	// Без неё таймер при разрыве связи печатал бы ошибку каждые 200 мс — лог утонул бы
	private const long ErrorLogCooldownMilliseconds = 2000;

	// Ссылка на детектор — источник снимков. Инжектируется через конструктор
	private readonly ObjectDetector _detector;

	// Ссылка на Bluetooth-сервис — выполняет фактическую отправку строк
	private readonly IBluetoothService _bluetoothService;

	// Замок для одновременного доступа к _latestSnapshot:
	// снимок кладёт поток камеры (событие SnapshotReady), а читает — поток таймера
	private readonly object _lock = new();

	// Таймер отправки: тикает каждые 200 мс, пока передача включена
	private readonly System.Threading.Timer _timer;

	// Последний полученный снимок распознавания. null, пока не пришёл ни один кадр
	private DetectionSnapshot? _latestSnapshot;

	// Флаг: тик таймера уже исполняется. Не даёт наложиться двум отправкам,
	// если одна итерация вдруг займёт больше 200 мс
	private bool _sending;

	// Момент последней записи ошибки в лог (для кулдауна)
	private long _lastErrorLogTimestamp;

	// Событие: очередная строка-пакет успешно отправлена устройству.
	// Подписчик — чат: он показывает эти строки как исходящие сообщения,
	// чтобы было видно, что именно уходит в эфир
	public event EventHandler<string>? PacketSent;

	// Конструктор: DI внедряет детектор и Bluetooth-сервис,
	// подписываемся на снимки и создаём (пока остановленный) таймер
	public DetectionTransmitter(ObjectDetector detector, IBluetoothService bluetoothService)
	{
		// Сохраняем зависимости
		_detector = detector;
		_bluetoothService = bluetoothService;

		// Подписываемся на результаты распознавания.
		// Событие приходит с потока камеры, но в обработчике мы только
		// копируем ссылку под замком — никакой работы с UI
		_detector.SnapshotReady += OnSnapshotReady;

		// Создаём таймер в остановленном состоянии (Infinite — не тикать).
		// Запускается он только при включении передачи (см. OnIsTransmissionEnabledChanged)
		_timer = new System.Threading.Timer(TimerTick, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
	}

	// [ObservableProperty] генерирует свойство IsTransmissionEnabled
	[ObservableProperty]

	// Флаг: включена ли передача. Ставится переключателем «Подача в Bluetooth»
	// на экране камеры. Когда выключен — таймер не тикает вовсе, а не просто
	// пропускает тик: так передача не жжёт батарею и не шлёт «хвосты»
	private bool _isTransmissionEnabled;

	// Вызывается фреймворком при каждом изменении IsTransmissionEnabled.
	// Запускает или останавливает таймер — это всё, что надо для «включить/выключить»
	partial void OnIsTransmissionEnabledChanged(bool value)
	{
		// Если передачу включили
		if (value)
		{
			// Запускаем таймер: первый тик сразу же (0 мс), дальше каждые 200 мс
			_timer.Change(0, SendPeriodMilliseconds);
			// Пишем в лог для отладки
			Debug.WriteLine("[DetectionTransmitter] Передача включена");
		}
		else
		{
			// Останавливаем таймер. Infinite — «не запускать больше никогда, пока не Change»
			_timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
			// Пишем в лог для отладки
			Debug.WriteLine("[DetectionTransmitter] Передача выключена");
		}
	}

	// Обработчик события детектора «посчитан новый кадр».
	// ВАЖНО: вызывается на потоке камеры. Почему так ок: мы не трогаем UI,
	// а лишь заменяем ссылку на снимок под замком. Снимок — неизменяемый record,
	// поэтому его можно смело отдать таймерному потоку
	private void OnSnapshotReady(object? sender, DetectionSnapshot snapshot)
	{
		// Охраняем запись замком: таймер может читать в тот же момент
		lock (_lock)
		{
			// Заменяем «последний снимок» новым. Старый при этом просто перестаёт
			// быть результатом — сборщик мусора уберёт его, как только снимок
			// перестанет быть нужен всем подписчикам
			_latestSnapshot = snapshot;
		}
	}

	// Тик таймера (раз в 200 мс). Вызывается на потоке из пула потоков.
	//
	// Метод объявлен async void — это стандарт для обработчиков событий: 
	// вызывающий (таймер) не умеет ждать Task. Ошибки не даём «уронить»
	// приложение: каждая отправка оборачивается в try/catch, итог — в лог
	private async void TimerTick(object? state)
	{
		// Быстрые проверки до начала работы:
		//   • передача включена;
		//   • есть соединение с устройством;
		//   • предыдущий тик ещё не исполняется.
		// Если хоть одно условие не выполнено — выходим без действий
		if (!IsTransmissionEnabled || !_bluetoothService.IsConnected || _sending)
			return;

		// Помечаем, что тик исполняется (защита от наложения двух отправок)
		_sending = true;

		try
		{
			// Забираем последний снимок под замком. Если его ещё нет
			// (камера не присылала кадры) — нечего отправлять
			DetectionSnapshot? snapshot;
			lock (_lock)
			{
				snapshot = _latestSnapshot;
			}

			// Хоть один кадр ещё не приходил — выходим
			if (snapshot is null)
				return;

			// Строим пакеты согласно правилам протокола
			var packets = DetectionFrameSerializer.FormatPackets(snapshot);

			// Отправляем пакеты по очереди, строго в порядке S → … → E.
			// Последовательные await не дают строкам перемешаться:
			// следующая отправляется только после завершения предыдущей
			foreach (var packet in packets)
			{
				// Проверяем условия ещё раз прямо перед отправкой:
				// соединение могло прерваться посреди кадра
				if (!IsTransmissionEnabled || !_bluetoothService.IsConnected)
					break;

				// Отправляем строку устройству (сервис добавит перевод строки)
				try
				{
					await _bluetoothService.SendDataAsync(packet);

					// Строка ушла — сообщаем подписчикам (чату)
					PacketSent?.Invoke(this, packet);
				}
				catch (Exception ex)
				{
					// Ошибку логируем, но не чаще раза в пару секунд
					if (ShouldLogError())
						Debug.WriteLine($"[DetectionTransmitter] Ошибка отправки: {ex.Message}");

					// Дальше отправлять бесполезно — обрываем кадр
					break;
				}
			}
		}
		finally
		{
			// Снимаем флаг исполнения даже при ошибке:
			// иначе тик «застрял» бы навсегда, и передача бы остановилась
			_sending = false;
		}
	}

	// Освобождение ресурсов: вызывается DI при завершении приложения
	public void Dispose()
	{
		// Отписываемся от снимков детектора, чтобы передатчик не «текли» в памяти
		_detector.SnapshotReady -= OnSnapshotReady;

		// Останавливаем и освобождаем таймер
		_timer.Dispose();
	}

	// Решает, пора ли писать очередную ошибку в лог.
	// Кулдаун на 2 секунды: при разрыве связи тик падает каждые 200 мс,
	// и без ограничения лог получал бы сотни одинаковых записей в секунду
	private bool ShouldLogError()
	{
		// Текущее монотонное время (не зависит от перевода часов)
		var now = Environment.TickCount64;

		// Если с последней записи прошло меньше паузы — писать рано
		if (now - _lastErrorLogTimestamp < ErrorLogCooldownMilliseconds)
			return false;

		// Обновляем метку и разрешаем запись
		_lastErrorLogTimestamp = now;
		return true;
	}
}