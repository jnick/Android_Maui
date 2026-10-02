// Подключаем модель представления камеры: страница показывает накопленные
// результаты распознавания, которые накапливает именно она
using MauiBluethooth.Presentation.Camera.ViewModels;

// Объявляем пространство имён для представлений раздела «Распознавание»
namespace MauiBluethooth.Presentation.Detection.Views;

// Страница диагностики: замеры времени по этапам обработки кадра.
//
// Почему страница получает готовый CameraViewModel, а не собственный.
// Данные для этой страницы живут в детекторе, а детектор — один на приложение.
// Если бы страница получила свою ViewModel, пришлось бы либо дублировать
// состояние детектора, либо вводить отдельный общий объект-хранилище.
// Здесь проще: страница читает то же самое свойство LastDiagnostics,
// которое и оверлей камеры. Данные точно те же, а не копия
public partial class DiagnosticsPage : ContentPage
{
	// Ссылка на модель представления (внедряется конструктором из DI)
	private readonly CameraViewModel _viewModel;

	// Конструктор страницы: DI внедряет ViewModel
	public DiagnosticsPage(CameraViewModel viewModel)
	{
		// Инициализируем элементы интерфейса, объявленные в DiagnosticsPage.xaml
		InitializeComponent();

		// Сохраняем ViewModel в поле
		_viewModel = viewModel;

		// Указываем источник данных для всех {Binding} на странице
		BindingContext = _viewModel;
	}
}