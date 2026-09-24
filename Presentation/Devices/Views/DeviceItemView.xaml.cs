// Объявляем пространство имён для представлений (Views) раздела «Устройства»
namespace MauiBluethooth.Presentation.Devices.Views;

// Объявление частичного класса представления одного Bluetooth-устройства (наследует ContentView — один элемент списка)
public partial class DeviceItemView : ContentView
{
	// Конструктор представления — вызывается при создании элемента списка
	public DeviceItemView()
	{
		// Инициализирует компоненты, объявленные в XAML-файле DeviceItemView.xaml
		InitializeComponent();
	}
}