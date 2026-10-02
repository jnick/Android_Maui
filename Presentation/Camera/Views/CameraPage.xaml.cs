// Подключаем пространство имён с моделью представления камеры
using MauiBluethooth.Presentation.Camera.ViewModels;

// Объявляем пространство имён для представлений раздела «Камера»
namespace MauiBluethooth.Presentation.Camera.Views;

// Страница камеры. Здесь и только здесь живёт код, который нельзя выразить в XAML:
// обработчики жизненного цикла страницы и связывание ViewModel с нативными элементами.
// partial — потому что часть класса генерируется XAML-компилятором из CameraPage.xaml
public partial class CameraPage : ContentPage
{
	// Ссылка на модель представления (внедряется конструктором из DI)
	private readonly CameraViewModel _viewModel;

	// Конструктор страницы: DI внедряет ViewModel
	public CameraPage(CameraViewModel viewModel)
	{
		// Инициализируем элементы интерфейса, объявленные в CameraPage.xaml.
		// Пока этот метод НЕ вызван, поля x:Name (Preview, OverlayView) недоступны
		InitializeComponent();

		// Сохраняем ViewModel в поле
		_viewModel = viewModel;

		// Указываем источник данных для всех {Binding} на странице.
		// Свойства ViewModel создаются генератором [ObservableProperty] и подписываются
		// на PropertyChanged, поэтому XAML обновляется автоматически
		BindingContext = _viewModel;

		// Передаём камерный сервис нативному контролу предпросмотра.
		// Сделано в коде, а не только через {Binding}, потому что handler создаёт
		// нативный элемент (PreviewView / MediaPlayerElement) в момент своего создания —
		// значение обязано быть готово к этому моменту.
		// Второй вариант «{Binding CameraService}» в XAML тоже работает, но полагается на порядок
		// применения привязок, а присваивание в коде делает зависимость явной
		Preview.CameraService = _viewModel.CameraService;

		// Назначаем оверлею источник геометрии кадра и список найденных объектов.
		// Передаются лямбда-функции (Func<...>), а не сами значения:
		// на момент создания страницы кадров ещё нет, а оверлей перерисовывается часто.
		// Благодаря функциям оверлей всегда читает АКТУАЛЬНЫЕ данные
		OverlayView.Drawable = new CameraOverlayDrawable(
			() => _viewModel.FrameGeometry,
			() => _viewModel.Detections);

		// Подписываемся на уведомления ViewModel, чтобы вовремя перерисовывать оверлей.
		// Без этой подписки рамки нарисовались бы один раз и больше не обновлялись бы
		_viewModel.PropertyChanged += OnViewModelPropertyChanged;
	}

	// Страница появилась: пользователь открыл раздел «Камера»
	protected override async void OnAppearing()
	{
		// Вызываем базовую реализацию (в ней выполняется инфраструктура MAUI)
		base.OnAppearing();

		// Сообщаем ViewModel, что страница видима, и запускаем камеру.
		// ViewModel помечает страницу активной — от этого зависит, будет ли камера
		// поднята заново при возврате приложения из фона
		await _viewModel.OnAppearingAsync();
	}

	// Страница исчезла: пользователь ушёл на другой экран или нажал «назад»
	protected override async void OnDisappearing()
	{
		// СНАЧАЛА останавливаем камеру, и только потом вызываем base.
		// Камера — единственный эксклюзивный ресурс в приложении: её нужно освободить
		// как можно быстрее, чтобы другое приложение успело её открыть.
		// Если бы base вызывался первым, UI-поток успел бы заблокироваться на асинхронном коде
		await _viewModel.OnDisappearingAsync();

		// Теперь вызываем базовую реализацию
		base.OnDisappearing();
	}

	// Обработчик уведомлений ViewModel: свойство изменилось
	private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		// Перерисовываем оверлей в двух случаях.
		//
		// Первый — пришла новая геометрия кадра. Она определяет, где и как масштабировать рамки.
		// Свойство обновляется троттлингом (не чаще 10 раз в секунду) в ViewModel,
		// поэтому перерисовка получается нечастой и не нагружает UI
		//
		// Второй — пришёл новый список найденных объектов. Это происходит на каждом обработанном кадре,
		// то есть чаще, чем меняется геометрия, и без перерисовки рамки «замерли» бы на месте.
		//
		// Обратите внимание: перерисовка НЕ ждёт вычислений. Она лишь просит MAUI
		// нарисовать последний известный список, когда до UI дойдёт очередь.
		// Это позволяет интерфейсу оставаться отзывчивым, даже если модель считает медленно
		if (e.PropertyName is nameof(CameraViewModel.FrameGeometry) or nameof(CameraViewModel.Detections))
		{
			// Invalidate() просит MAUI перерисовать GraphicsView: вызовется Draw у нашего
			// CameraOverlayDrawable с новыми данными. Без этого кадр нарисуется только один раз
			OverlayView.Invalidate();
		}
	}
}
