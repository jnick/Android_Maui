// Подключаем пространство имён, где находятся модели данных приложения (в частности BluetoothDeviceInfo)
using MauiBluethooth.Models;

// Объявляем собственное пространство имён для сервисного слоя приложения
namespace MauiBluethooth.Services;

// Интерфейс — контракт Bluetooth-сервиса, который реализуется отдельно под каждую платформу (Android, Windows)
public interface IBluetoothService
{
	// Событие: найдено очередное Bluetooth-устройство. Передаёт подписчику информацию об устройстве
	event EventHandler<BluetoothDeviceInfo>? DeviceDiscovered;

	// Событие: процесс поиска устройств завершён (независимо от того, нашлись ли устройства)
	event EventHandler? DiscoveryFinished;

	// Запрос необходимых разрешений (Bluetooth, геолокация). Возвращает true, если доступ получен
	Task<bool> RequestPermissionsAsync();

	// Запуск поиска Bluetooth-устройств. Возвращает true, если поиск успешно запущен
	Task<bool> StartDiscoveryAsync();

	// Остановка поиска Bluetooth-устройств
	Task StopDiscoveryAsync();
}