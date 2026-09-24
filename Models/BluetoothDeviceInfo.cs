// Объявляем пространство имён, где хранятся модели данных приложения
namespace MauiBluethooth.Models;

// Модель данных, описывающая одно Bluetooth-устройство
public class BluetoothDeviceInfo
{
	// Имя устройства (init — задаётся только при создании; по умолчанию пустая строка)
	public string Name { get; init; } = string.Empty;

	// МАС-адрес Bluetooth-устройства (init; по умолчанию пустая строка)
	public string Address { get; init; } = string.Empty;

	// Признак того, что устройство сопряжено с данным устройством (init; по умолчанию false)
	public bool IsBonded { get; init; }
}