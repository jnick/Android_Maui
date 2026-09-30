// Подключаем пространство имён с коллекцией ObservableCollection (автообновляемая коллекция для привязок)
using System.Collections.ObjectModel;
// Подключаем базовые классы MVVM (ObservableObject, атрибут ObservableProperty)
using CommunityToolkit.Mvvm.ComponentModel;
// Подключаем компоненты для RelayCommand (команды, вызываемые из XAML)
using CommunityToolkit.Mvvm.Input;
// Подключаем наши модели данных камеры (CameraOption, CameraFrame, CameraFrameGeometry)
using MauiBluethooth.Models.Camera;
// Подключаем интерфейс камерного сервиса
using MauiBluethooth.Services;
// Подключаем MainThread — он нужен, чтобы менять свойства привязки только в UI-потоке
using Microsoft.Maui.ApplicationModel;

// Объявляем пространство имён для моделей представления раздела «Камера»
namespace MauiBluethooth.Presentation.Camera.ViewModels;

// Модель представления экрана камеры.
// partial — потому что атрибуты [ObservableProperty] и [RelayCommand] из CommunityToolkit.Mvvm
// кодогенерируют дополнительную часть класса. IDisposable нужен, чтобы отписаться от события кадров
public partial class CameraViewModel : ObservableObject, IDisposable
{
	// Ссылка на камерный сервис (инжектируется через конструктор из DI)
	private readonly ICameraService _cameraService;

	// Флаг: мы САМИ выставляем SelectedCamera при обновлении списка.
	// Нужен, чтобы не запускать переключение камеры в ответ на собственное же присваивание —
	// иначе после каждого запуска камера «переключалась бы сама на себя»
	private bool _suppressCameraSelection;

	// Момент последнего обновления FrameGeometry в миллисекундах (для троттлинга обновлений)
	private long _lastGeometryUpdate;

	// Конструктор: DI внедряет камерный сервис
	public CameraViewModel(ICameraService cameraService)
	{
		// Сохраняем сервис в приватное поле
		_cameraService = cameraService;

		// Подписываемся на кадры. Событие приходит с фонового потока платформы, 15–30 раз в секунду.
		// Обработчик (OnFrameReceived) специально ничего тяжёлого не делает — только троттлит геометрию
		_cameraService.FrameReceived += OnFrameReceived;
	}

	// Отдаём сервис наружу: он нужен не только ViewModel, но и самому контролу CameraPreview
	// (XAML биндит {Binding CameraService}). Отдельное свойство, а не доступ к приватному полю
	public ICameraService CameraService => _cameraService;

	// Список камер для Picker. Именно ObservableCollection, а не List:
	// при его замене внутренних элементов интерфейс перерисовывается автоматически
	public ObservableCollection<CameraOption> CameraOptions { get; } = new();

	// Резервное поле выбранной камеры.
	// Свойство SelectedCamera не помечено [ObservableProperty], потому что у него есть
	// дополнительная логика в сеттере — реакция на смену камеры
	private CameraOption? _selectedCamera;

	// Выбранная камера. Сеттер написан вручную, потому что помимо уведомления UI
	// нужно ещё и переключить камеру в сервисе
	public CameraOption? SelectedCamera
	{
		// Геттер просто отдаёт значение поля
		get => _selectedCamera;

		// Сеттер вызывается из Picker, когда пользователь выбрал другую камеру
		set
		{
			// SetProperty сам сравнит новое и старое значение, обновит поле
			// и вызовет PropertyChanged. Если значение не изменилось — возвращает false
			if (SetProperty(ref _selectedCamera, value))
			{
				// Значение реально изменилось — реагируем на это
				OnSelectedCameraChanged(value);
			}
		}
	}

	// [ObservableProperty] генерирует публичное свойство IsStarting (идёт ли запуск камеры)
	[ObservableProperty]

	// Флаг: прямо сейчас камера запускается. Пока он true, повторные нажатия игнорируются
	private bool _isStarting;

	// [ObservableProperty] генерирует публичное свойство IsRunning (работает ли камера)
	[ObservableProperty]

	// Флаг: камера запущена и отдаёт кадры. Управляет видимостью кнопки «Остановить»
	private bool _isRunning;

	// [ObservableProperty] генерирует публичное свойство IsCameraPickerVisible (показывать ли выбор камеры)
	[ObservableProperty]

	// Флаг: показывать ли Picker выбора камеры. Скрываем, если камера всего одна — выбор бессмыслен
	private bool _isCameraPickerVisible;

	// [ObservableProperty] генерирует публичное свойство ShowPermissionSettings (показать кнопку настроек)
	[ObservableProperty]

	// Флаг: пользователь отказал в доступе к камере.
	// Повторный запрос разрешения уже не поможет, поэтому показываем кнопку «Открыть настройки»
	private bool _showPermissionSettings;

	// [ObservableProperty] генерирует публичное свойство StatusText (текст статуса под видео)
	[ObservableProperty]

	// Текст статуса: «Ожидание», «Запуск камеры...», «Камера запущена», текст ошибки и т.д.
	private string _statusText = "Ожидание";

	// [ObservableProperty] генерирует публичное свойство FrameGeometry (геометрия текущего кадра)
	[ObservableProperty]

	// Геометрия последнего кадра. По ней оверлей (CameraOverlayDrawable) рисует рамку
	// в тех же координатах, что и видео. null, пока кадров не было
	private CameraFrameGeometry? _frameGeometry;

	// Вызывается страницей из OnAppearing: страница открылась
	public async Task OnAppearingAsync()
	{
		// Сообщаем сервису, что страница видима.
		// Это условие в HandleAppResumeAsync: возвращаться из фона с камерой имеет смысл только здесь
		_cameraService.SetPageActive(true);

		// Запускаем камеру
		await StartCameraAsync();
	}

	// Вызывается страницей из OnDisappearing: пользователь ушёл на другой экран
	public async Task OnDisappearingAsync()
	{
		// Сообщаем сервису, что страница скрыта — при возврате из фона камера не поднимется
		_cameraService.SetPageActive(false);

		// Останавливаем камеру и освобождаем её.
		// Критично: если этого не сделать, камера останется занятой и другое приложение её не откроет
		await _cameraService.StopAsync();

		// Синхронизируем UI с реальным состоянием: кнопка «Остановить» должна исчезнуть
		IsRunning = false;
	}

	// [RelayCommand] генерирует команду StartCameraCommand для вызова из XAML
	[RelayCommand]

	// Команда «Запустить камеру». Прямая обёртка над общим методом запуска
	private Task StartCamera() => StartCameraAsync();

	// [RelayCommand] генерирует команду StopCameraCommand (кнопка «Остановить»)
	[RelayCommand]

	// Команда «Остановить камеру»
	private async Task StopCamera()
	{
		// Просим сервис освободить камеру
		await _cameraService.StopAsync();

		// Обновляем UI: камера больше не работает
		IsRunning = false;

		// Пишем понятный статус вместо технического
		StatusText = "Остановлено";
	}

	// [RelayCommand] генерирует команду OpenSettingsCommand (кнопка «Открыть настройки»)
	[RelayCommand]

	// Команда открытия системных настроек приложения — единственный способ выдать разрешение,
	// если пользователь уже отказал. Метод статический: ему не нужен доступ к полям ViewModel
	private static void OpenSettings() => AppInfo.ShowSettingsUI();

	// Освобождение ресурсов: вызывается, когда DI уничтожает сервис
	public void Dispose()
	{
		// ОБЯЗАТЕЛЬНО отписываемся от события кадров.
		// Без этого сервис-синглтон удерживал бы ссылку на ViewModel в памяти
		_cameraService.FrameReceived -= OnFrameReceived;
	}

	// Реакция на смену выбранной камеры пользователем
	private void OnSelectedCameraChanged(CameraOption? value)
	{
		// Проверяем два условия:
		// _suppressCameraSelection — присваивание сделали мы сами (обновление списка), реагировать не нужно;
		// value is not null — пустым значением сеттер не вызывается
		if (!_suppressCameraSelection && value is not null)
		{
			// Запускаем переключение «в фоне» (fire-and-forget).
			// Сеттер свойства не может быть async, поэтому задачу не ждём и исключения не наблюдаем:
			// сам метод ChangeCameraAsync обрабатывает свои ошибки и сам пишет статус
			_ = ChangeCameraAsync(value);
		}
	}

	// Общий метод запуска камеры (используется и командой, и OnAppearingAsync)
	private async Task StartCameraAsync()
	{
		// Защита от повторного запуска: пока камера включается ИЛИ уже включена — ничего не делаем.
		// Без этой проверки двойной тап по кнопке создал бы две гонки за камеру
		if (IsStarting || IsRunning)
		{
			return;
		}

		// Показываем индикатор загрузки и блокируем повторные нажатия
		IsStarting = true;

		// Информируем пользователя о том, что происходит
		StatusText = "Запуск камеры...";

		// Просим сервис запустить камеру. Он сам запросит разрешение, найдёт камеры и вернёт результат
		var result = await _cameraService.StartAsync();

		// Обновляем список камер: сервис обнаруживает их только при первом запуске
		RefreshCameraOptions();

		// Гасим индикатор загрузки
		IsStarting = false;

		// Состояние работы камеры берём ИЗ РЕЗУЛЬТАТА, а не оптимистично: запуск мог провалиться
		IsRunning = result.Success;

		// Показываем кнопку «Открыть настройки» только если дело именно в запрете разрешения.
		// При других ошибках (камера занята другим приложением) настройки не помогут
		ShowPermissionSettings = !result.Success && _cameraService.PermissionDenied;

		// Показываем результат: либо «Камера запущена», либо текст ошибки от сервиса.
		// «??» — если сервис не вернул текст ошибки, подставляем свой запасной вариант
		StatusText = result.Success ? "Камера запущена" : result.ErrorMessage ?? "Не удалось запустить камеру";
	}

	// Переключение на другую камеру, выбранную в Picker
	private async Task ChangeCameraAsync(CameraOption camera)
	{
		// Если выбрана та же камера, что уже открыта — ничего не делаем.
		// Страховка на случай, когда сработает присваивание SelectedCamera при обновлении списка
		if (camera.Id == _cameraService.SelectedCameraId)
		{
			return;
		}

		// Информируем пользователя: идёт переключение (оно не мгновенное — камера переоткрывается)
		StatusText = "Переключение камеры...";

		// Просим сервис переключиться. Он внутри останавливает старую камеру и запускает новую
		var selected = await _cameraService.SelectCameraAsync(camera.Id);

		// Успешное переключение: камера работает
		if (selected)
		{
			// Камера снова работает — возвращаем кнопку «Остановить»
			IsRunning = true;

			// Убираем служебный статус
			StatusText = "Камера запущена";
		}
		else
		{
			// Не удалось (камера не отвечает или отключилась) — сообщаем об этом
			StatusText = "Не удалось переключить камеру";
		}
	}

	// Переносит список камер из сервиса в коллекцию, доступную UI
	private void RefreshCameraOptions()
	{
		// Включаем подавление переключения: сейчас мы меняем SelectedCamera не по воле пользователя,
		// а сами, чтобы просто отметить активную камеру в Picker. Переключать камеру тут не нужно
		_suppressCameraSelection = true;

		// Очищаем коллекцию: Picker не умеет сам подхватывать замену коллекции,
		// поэтому элементы удаляются поштучно
		CameraOptions.Clear();

		// Копируем камеры из сервиса в нашу коллекцию
		foreach (var option in _cameraService.CameraOptions)
		{
			// Добавляем по одной — ObservableCollection уведомляет UI на каждом добавлении
			CameraOptions.Add(option);
		}

		// Выбираем в Picker именно ту камеру, которую открыл сервис.
		// Если по идентификатору найти не удалось (расхождение списков) — берём первую доступную
		SelectedCamera = CameraOptions.FirstOrDefault(option => option.Id == _cameraService.SelectedCameraId)
			?? CameraOptions.FirstOrDefault();

		// Снимаем подавление: с этого момента смена выбора пользователем снова вызовет переключение
		_suppressCameraSelection = false;

		// Picker показываем только если камер больше одной.
		// При одной камере выбирать нечего, и лишний контрол только мешает
		IsCameraPickerVisible = CameraOptions.Count > 1;
	}

	// Обработчик события «пришёл кадр» из сервиса.
	// ВАЖНО: вызывается на фоновом потоке платформы (Android-однопоточный исполнитель или поток Windows),
	// а свойства привязки менять разрешено только в UI-потоке
	private void OnFrameReceived(object? sender, CameraFrame frame)
	{
		// Текущее время в миллисекундах. TickCount64 монотонен и не «прыгает» при переводе часов,
		// в отличие от DateTime.Now
		var now = Environment.TickCount64;

		// ТРОТТЛИНГ: событие приходит 15–30 раз в секунду, а менять свойство привязки
		// так часто бессмысленно и дорого. Два условия пропуска:
		//   1) с прошлого обновления прошло меньше 100 мс — ограничиваем частоту;
		//   2) геометрия равна прежней (record сравнивает по значениям) — уведомлять UI незачем
		if (now - _lastGeometryUpdate < 100 || FrameGeometry == frame.Geometry)
		{
			return;
		}

		// Запоминаем момент обновления
		_lastGeometryUpdate = now;

		// Переключаемся в UI-поток: только там можно присваивать свойства, на которые завязаны биндинги.
		// BeginInvokeOnMainThread (а не InvokeOnMainThread) не ждёт выполнения — обработчик кадров
		// не должен блокироваться на UI-потоке, иначе видео начнёт подтормаживать
		MainThread.BeginInvokeOnMainThread(() => FrameGeometry = frame.Geometry);
	}
}
