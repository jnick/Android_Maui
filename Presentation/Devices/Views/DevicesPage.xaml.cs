// Подключаем пространство имён, где находится модель представления (ViewModel) этого экрана
using MauiBluethooth.Presentation.Devices.ViewModels;

// Объявляем пространство имён для представлений раздела «Устройства»
namespace MauiBluethooth.Presentation.Devices.Views;

// Объявление частичного класса страницы со списком Bluetooth-устройств (наследует ContentPage — полноэкранная страница)
public partial class DevicesPage : ContentPage
{
	// Конструктор страницы. ViewModel передаётся через внедрение зависимостей (DI)
	public DevicesPage(DevicesViewModel viewModel)
	{
		// Инициализирует компоненты, объявленные в XAML-файле DevicesPage.xaml
		InitializeComponent();
		// Привязываем ViewModel как контекст данных страницы — всё, что в ней, доступно из XAML-привязок
		BindingContext = viewModel;
	}
}