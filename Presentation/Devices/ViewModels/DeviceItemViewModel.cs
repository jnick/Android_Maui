// Подключаем пространство имён: базовые классы для MVVM (ObservableObject, атрибут ObservableProperty)
using CommunityToolkit.Mvvm.ComponentModel;
// Подключаем пространство имён с моделью данных Bluetooth-устройства
using MauiBluethooth.Models;

// Объявляем пространство имён для моделей представления раздела «Устройства»
namespace MauiBluethooth.Presentation.Devices.ViewModels;

// Модель представления одного Bluetooth-устройства в списке. 
// partial + ObservableObject — Toolkit генерирует INotifyPropertyChanged для атрибутов [ObservableProperty]
public partial class DeviceItemViewModel : ObservableObject
{
	// [ObservableProperty] генерирует публичное свойство Name с уведомлением об изменении
	[ObservableProperty]
	// Поле, хранящее имя устройства (по умолчанию пустая строка)
	private string name = string.Empty;

	// [ObservableProperty] генерирует публичное свойство Address (MAC-адрес устройства)
	[ObservableProperty]
	// Поле, хранящее адрес Bluetooth-устройства
	private string address = string.Empty;

	// [ObservableProperty] генерирует публичное свойство Status (текстовый статус, например «Сопряжено»)
	[ObservableProperty]
	// Поле, хранящее статус устройства
	private string status = string.Empty;

	// Конструктор: создаёт VM на основе данных, полученных о реальном устройстве
	public DeviceItemViewModel(BluetoothDeviceInfo device)
	{
		// Заполняем свойства VM данными устройства
		Update(device);
	}

	// Метод обновления VM новыми данными (например, устройство меняет статус во время поиска)
	public void Update(BluetoothDeviceInfo device)
	{
		// Имя устройства; если имя пустое — подставляем русское «Неизвестное устройство»
		Name = string.IsNullOrWhiteSpace(device.Name) ? "Неизвестное устройство" : device.Name;
		// Присваиваем адрес устройства
		Address = device.Address;
		// Если устройство сопряжено (bonded) — показываем «Сопряжено», иначе пустую строку
		Status = device.IsBonded ? "Сопряжено" : string.Empty;
	}
}