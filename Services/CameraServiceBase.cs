// Подключаем пространство имён с моделями кадра, камер и результата запуска
using MauiBluethooth.Models.Camera;
// Подключаем API разрешений MAUI (Permissions.Camera, PermissionStatus)
using Microsoft.Maui.ApplicationModel;

// Объявляем собственное пространство имён для сервисного слоя приложения
namespace MauiBluethooth.Services;

// Общая (платформенно-независимая) часть камерного сервиса.
// Здесь собрана вся логика, одинаковая для Android и Windows: захват семафора, запрос разрешений,
// учёт пожеланий пользователя («камера должна работать»), перезапуск при возврате из фона.
// Платформенные классы наследуются от него и реализуют 4 абстрактных метода внизу файла
public abstract class CameraServiceBase : ICameraService
{
	// Семафор с одним «слотом» — взаимное исключение для асинхронных операций с камерой.
	// Зачем: старт, остановка, переключение камеры, сон и пробуждение могут прийти почти одновременно
	// (пользователь быстро жмёт кнопки или сворачивает приложение). Без семафора платформенные вызовы
	// пересекутся, и на Android получим «IllegalStateException», а на Windows — утечку MediaCapture
	private readonly SemaphoreSlim _gate = new(1, 1);

	// Список камер, найденных на устройстве. Инициализируется пустым, чтобы не было null
	private IReadOnlyList<CameraOption> _cameraOptions = Array.Empty<CameraOption>();

	// Идентификатор выбранной камеры (null, пока платформа не инициализирована)
	private string? _selectedCameraId;

	// Флаг: страница камеры сейчас на экране (задаётся из SetPageActive)
	private bool _pageActive;

	// Флаг «пожелание пользователя»: камера ДОЛЖНА работать. Ставится в StartAsync и сбрасывается в StopAsync.
	// Нужен, чтобы отличить «остановили осознанно» от «остановили, потому что ушли в фон»
	private bool _wantsRunning;

	// Флаг: платформенная часть (CameraX / MediaFrameSourceGroup) уже инициализирована.
	// Инициализация выполняется один раз — повторный поиск камер при каждом старте не нужен
	private bool _platformInitialized;

	// Событие: пришёл очередной кадр. Вызывается с фонового потока платформы
	public event EventHandler<CameraFrame>? FrameReceived;

	// Список доступных камер (только для чтения снаружи — список заменяется целиком, а не меняется)
	public IReadOnlyList<CameraOption> CameraOptions => _cameraOptions;

	// Идентификатор выбранной камеры
	public string? SelectedCameraId => _selectedCameraId;

	// Геометрия последнего кадра. Обновляется здесь же в RaiseFrame, чтобы UI мог прочитать её синхронно
	public CameraFrameGeometry? FrameGeometry { get; private set; }

	// Признак того, что камера запущена
	public bool IsRunning { get; private set; }

	// Признак отказа в разрешении на камеру
	public bool PermissionDenied { get; private set; }

	// Создаёт нативный элемент предпросмотра — делегирует платформенной реализации
	public object CreatePlatformPreview() => CreatePlatformPreviewCore();

	// Запуск камеры
	public async Task<CameraStartResult> StartAsync()
	{
		// Занимаем семафор: дальше до освобождения лока никакая другая операция с камерой не пройдёт
		await _gate.WaitAsync();

		try
		{
			// Запоминаем намерение пользователя: камера должна работать.
			// Именно по этому флагу HandleAppResumeAsync решит, стоит ли её включать после возврата из фона
			_wantsRunning = true;

			// Если камера уже работает — ничего не делаем (повторный тап по кнопке «Старт» должен быть безвреден)
			if (IsRunning)
			{
				return new CameraStartResult(true);
			}

			// Проверяем/запрашиваем разрешение на камеру. Без него ОС не даст открыть камеру
			if (!await RequestPermissionAsync())
			{
				// Помечаем, что разрешения нет, — UI покажет кнопку «Открыть настройки»
				PermissionDenied = true;
				return new CameraStartResult(false, "Разрешение на камеру не получено");
			}

			// Разрешение получено — сбрасываем прежний флаг отказа
			PermissionDenied = false;

			// Делегируем реальный запуск платформенной реализации
			return await StartCoreAsync();
		}
		catch (Exception exception)
		{
			// Любая ошибка платформы (нет камеры, устройство занято другим приложением) превращается
			// в результат с текстом ошибки, а не в необработанное исключение, ломающее UI.
			// В отладчике дополнительно пишем полный стек: по одному Message не понять,
			// где именно сломалось (разрешение, CameraX, создание анализатора, bind)
			System.Diagnostics.Debug.WriteLine($"[CameraService] Старт камеры завершился ошибкой: {exception}");
			return new CameraStartResult(false, exception.Message);
		}
		finally
		{
			// Освобождаем семафор в любом случае — иначе следующий вызов навсегда зависнет
			_gate.Release();
		}
	}

	// Остановка камеры
	public async Task StopAsync()
	{
		// Занимаем семафор, чтобы не пересечься с возможным параллельным запуском
		await _gate.WaitAsync();

		try
		{
			// Сбрасываем намерение: после возврата из фона камера автоматически НЕ поднимется
			_wantsRunning = false;

			// Делегируем освобождение ресурсов платформе (UnbindAll на Android, Dispose MediaCapture на Windows)
			await StopCoreAsync();
		}
		finally
		{
			// Освобождаем семафор
			_gate.Release();
		}
	}

	// Переключение на другую камеру по идентификатору из списка CameraOptions
	public async Task<bool> SelectCameraAsync(string cameraId)
	{
		// Занимаем семафор: переключение — это «остановить старую + запустить новую», две операции,
		// которые не должны выполняться одновременно с другими
		await _gate.WaitAsync();

		try
		{
			// Проверяем, что такая камера вообще есть. Позволяет отсечь «битый» идентификатор из устаревшего UI
			if (_cameraOptions.All(option => option.Id != cameraId))
			{
				return false;
			}

			// Запоминаем новый выбор. Если камера не запущена, этого достаточно
			_selectedCameraId = cameraId;
			if (!IsRunning)
			{
				return true;
			}

			// Камера работает: сначала освобождаем старую (на Android нужно UnbindAll, иначе вторая
			// привязка к ProcessCameraProvider упадёт), затем запускаем новую
			await StopCoreAsync();
			var result = await StartCoreAsync();

			// Возвращаем успех именно запуска, а не самого выбора
			return result.Success;
		}
		catch
		{
			// Ошибка переключения (камера не отвечает) не должна пробрасываться в обработчик команды UI
			return false;
		}
		finally
		{
			// Освобождаем семафор
			_gate.Release();
		}
	}

	// Сообщаем сервису, видима ли страница камеры
	public void SetPageActive(bool isActive)
	{
		// Просто запоминаем флаг — синхронизация здесь не нужна, поле типа bool атомарно
		_pageActive = isActive;
	}

	// Приложение уходит в фон
	public async Task HandleAppSleepAsync()
	{
		// Занимаем семафор
		await _gate.WaitAsync();

		try
		{
			// Освобождаем камеру. _wantsRunning НЕ трогаем: пользователь её не выключал, он просто свернул приложение.
			// Это важное отличие от StopAsync — именно поэтому в фоне камера поднимется обратно
			await StopCoreAsync();
		}
		finally
		{
			// Освобождаем семафор
			_gate.Release();
		}
	}

	// Приложение вернулось из фона
	public async Task HandleAppResumeAsync()
	{
		// Занимаем семафор
		await _gate.WaitAsync();

		try
		{
			// Три причины НЕ поднимать камеру:
			// _wantsRunning == false — пользователь сам остановил камеру до ухода в фон;
			// _pageActive == false — страница камеры закрыта, а значит кадры и не нужны;
			// IsRunning == true — камера уже работает (например, iOS-подобный сценарий без сна)
			if (!_wantsRunning || !_pageActive || IsRunning)
			{
				return;
			}

			// Разрешение могли отозвать, пока приложение было в фоне — проверяем заново
			if (!await RequestPermissionAsync())
			{
				// Не выдалось — ставим флаг, чтобы UI предложил открыть настройки
				PermissionDenied = true;
				return;
			}

			// Запускаем поток кадров заново
			await StartCoreAsync();
		}
		catch
		{
			// Приложение только возвращается из фона: сюда может попасть исключение от платформенного кода.
			// Молча игнорируем — состояние покажет IsRunning, а пользователь сможет нажать «Старт»
		}
		finally
		{
			// Освобождаем семафор
			_gate.Release();
		}
	}

	// Сохраняет список камер, найденных платформой, и выбирает камеру по умолчанию
	protected void SetCameraOptions(IEnumerable<CameraOption> options)
	{
		// Материализуем в массив: перечислитель от платформенного кода может быть одноразовым,
		// а список камер нужно читать много раз (UI, Picker, проверки)
		_cameraOptions = options.ToArray();

		// Если ранее выбранная камера отсутствует в новом списке (например, камера отключили),
		// выбираем новую по умолчанию
		if (_cameraOptions.All(option => option.Id != _selectedCameraId))
		{
			// Предпочитаем заднюю камеру (обычная «основная»), а если её нет — первую доступную
			_selectedCameraId = _cameraOptions.FirstOrDefault(option => option.LensFacing == CameraLensFacing.Rear)?.Id
				?? _cameraOptions.FirstOrDefault()?.Id;
		}
	}

	// Оповещает подписчиков о новом кадре
	protected void RaiseFrame(CameraFrame frame)
	{
		// Запоминаем геометрию: оверлею она нужна даже без подписки на событие кадров
		FrameGeometry = frame.Geometry;

		// Рассылаем кадр подписчикам. «?.Invoke» — если подписчиков нет, ничего не делаем
		FrameReceived?.Invoke(this, frame);
	}

	// АБСТРАКТНЫЕ ЧЛЕНЫ: их реализуют платформенные наследники (AndroidCameraService, WindowsCameraService).
	// В базовом классе они объявлены, чтобы общая логика выше могла вызывать платформенный код,
	// не зная, какая именно платформа под ней

	// Создать нативный элемент предпросмотра
	protected abstract object CreatePlatformPreviewCore();

	// Одноразовая инициализация платформы: найти камеры, запомнить их список
	protected abstract Task InitializePlatformAsync();

	// Начать отдавать кадры выбранной камеры
	protected abstract Task StartPlatformAsync();

	// Остановить отдачу кадров и освободить нативные ресурсы
	protected abstract Task StopPlatformAsync();

	// Внутренний запуск, вызывается из StartAsync / HandleAppResumeAsync / SelectCameraAsync.
	// Фокус в том, что разрешение и семафор уже обработаны снаружи
	private async Task<CameraStartResult> StartCoreAsync()
	{
		// Первый запуск: инициализируем платформу (поиск камер) ровно один раз
		if (!_platformInitialized)
		{
			await InitializePlatformAsync();
			_platformInitialized = true;
		}

		// Не нашли ни одной камеры (нет устройства, нет разрешения, камера отключена) — запускать нечего
		if (_cameraOptions.Count == 0 || string.IsNullOrEmpty(_selectedCameraId))
		{
			return new CameraStartResult(false, "Камера не найдена");
		}

		// Делегируем платформенный запуск: bind use cases на Android, MediaCapture на Windows
		await StartPlatformAsync();

		// Камера запущена — фиксируем состояние
		IsRunning = true;

		// Успех
		return new CameraStartResult(true);
	}

	// Внутренняя остановка. Вызывается из StopAsync, HandleAppSleepAsync и SelectCameraAsync
	private async Task StopCoreAsync()
	{
		// Если камера не работает, но платформа уже инициализирована — останавливать нечего
		if (!IsRunning && _platformInitialized)
		{
			return;
		}

		try
		{
			// Делегируем платформенную остановку
			await StopPlatformAsync();
		}
		finally
		{
			// Состояние сбрасываем даже при ошибке, чтобы UI не остался в состоянии «работает»,
			// когда камера уже не отдаёт кадры
			IsRunning = false;
		}
	}

	// Проверяет и запрашивает разрешение на камеру через единый API MAUI (работает на всех платформах)
	private static async Task<bool> RequestPermissionAsync()
	{
		// Спрашиваем текущий статус разрешения (без показа диалога)
		var status = await Permissions.CheckStatusAsync<Permissions.Camera>();

		// Разрешение уже выдано — ничего делать не нужно
		if (status == PermissionStatus.Granted)
		{
			return true;
		}

		// Пользователь отказал и выбрал «больше не спрашивать» — повторный запрос не поможет,
		// нужно отправлять пользователя в настройки ОС
		if (status == PermissionStatus.Denied)
		{
			return false;
		}

		// Статус Unknown (или Restricted на iOS) — показываем системный диалог запроса
		status = await Permissions.RequestAsync<Permissions.Camera>();

		// Проверяем итог запроса
		return status == PermissionStatus.Granted;
	}
}
