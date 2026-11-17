using CommunityToolkit.Mvvm.ComponentModel;

namespace MOIRAI.ViewModels;

public partial class LocationPickerViewModel : ViewModelBase
{
    public string LocationName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    private double _pinLon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    private double _pinLat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    private bool _hasPin;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoordinatesText))]
    private string _pinIcao = "";

    public LocationPickerViewModel(string locationName, double? lon, double? lat, string? icao)
    {
        LocationName = string.IsNullOrWhiteSpace(locationName) ? "location" : locationName;
        if (lon is { } lo && lat is { } la)
        {
            _pinLon = lo;
            _pinLat = la;
            _pinIcao = icao ?? "";
            _hasPin = true;
        }
    }

    public string CoordinatesText => HasPin
        ? (string.IsNullOrEmpty(PinIcao) ? "" : $"{PinIcao} · ") +
          $"{Math.Abs(PinLat):0.0000}° {(PinLat >= 0 ? "N" : "S")},  {Math.Abs(PinLon):0.0000}° {(PinLon >= 0 ? "E" : "W")}"
        : "No pin placed yet.";
}
