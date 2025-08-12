using System.Text.Json;
using Buttplug.Client;
using Buttplug.Client.Connectors.WebsocketConnector;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Timer = System.Timers.Timer;
using NCalc;
using NCalc.Handlers;
using Newtonsoft.Json.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace BPGE;

// TODO: some kind of error catcher

public class YamlConverterNCalcExpression : IYamlTypeConverter
{
    public bool Accepts(Type type)
    {
        return type == typeof(Expression);
    }

    public object? ReadYaml(IParser parser, Type type)
    {
        var scalar = parser.Consume<Scalar>();
        Expression expr = new Expression(scalar.Value);
        expr.EvaluateParameter += (string name, ParameterArgs args) =>
        {
            if (name.Contains("."))
            {
                var parts = name.Split(".");
                var current = expr.Parameters[parts[0]];

                // TODO: clean and remove what can't be a case
                for (int i = 1; i < parts.Length && current != null; i++)
                {
                    switch (current)
                    {
                        case JObject jo:
                            current = jo[parts[i]];
                            break;
                        case JValue jv:
                            current = jv.Value;
                            break;
                        case IDictionary<string, object> dict when dict.ContainsKey(parts[i]):
                            current = dict[parts[i]];
                            break;
                        default:
                        {
                            //var dyn = current as dynamic;
                            //if (dyn != null)
                            if (current is dynamic)
                                current = ((dynamic)current)[parts[i]];
                            else
                                current = null;
                            break;
                        }
                    }
                }
                
                if (current is JValue jv2)
                    args.Result = jv2.Value;
                else
                    args.Result = current;
            }
            else if (expr.Parameters.TryGetValue(name, out var parameter))
            {
                if (parameter is JValue jv)
                    args.Result = jv.Value;
                else
                    args.Result = parameter;
            }
        };
        return expr;
    }

    // Not used, will need work to be uses
    public void WriteYaml(IEmitter emitter, object? value, Type type)
    {
        var expression = (Expression)value!;
        emitter.Emit(new Scalar(expression.ExpressionString!));
    }
}

public class EventModifier
{
    public Expression Condition { get; set; }
    public bool CheckCondition(dynamic dataValue, int intensity, double duration)
    {
        if (Condition != null)
        {
            if (dataValue is JObject jo)
                Condition.Parameters["value"] = jo;
            else if (dataValue is JValue jv)
                Condition.Parameters["value"] = jv;
            else
                Condition.Parameters["value"] = dataValue;
            Condition.Parameters["intensity"] = intensity;
            Condition.Parameters["duration"] = duration;
        }
        
        return Condition == null || (bool)Condition.Evaluate()!;
    }
    
    public Expression Intensity { get; set; }
    public Expression Duration { get; set; }

    public int EvaluateIntensity(dynamic dataValue, int intensity, double duration)
    {
        int output = (int)EvaluateExpression(Intensity, dataValue, intensity, duration);

        // TODO: add config for min and max variable (to avoid hardcoding default values)
        IntensityMin = Math.Clamp(IntensityMin, 0, 100);
        IntensityMax = Math.Clamp(IntensityMax, 0, 100);
        
        return Math.Clamp(output, IntensityMin, IntensityMax);
    }
    public double EvaluateDuration(dynamic dataValue, int intensity, double duration)
    {
        double output = EvaluateExpression(Duration, dataValue, intensity, duration);
        
        // TODO: add config for min and max variable (to avoid hardcoding default values)
        DurationMin = Math.Clamp(DurationMin, 0, 300);
        DurationMax = Math.Clamp(DurationMax, 0, 300);
        
        return Math.Clamp(output, IntensityMin, IntensityMax);
    }
    private double EvaluateExpression(Expression expression, dynamic dataValue, int intensity, double duration)
    {
        expression.Parameters["value"] = dataValue switch
        {
            JObject jo => jo,
            JValue jv => jv,
            _ => dataValue
        };;
        expression.Parameters["intensity"] = intensity;
        expression.Parameters["duration"] = duration;
        
        double result = Convert.ToDouble(expression.Evaluate());

        return result;
    }
    
    public int IntensityMin { get; set; } = 0;
    public int IntensityMax { get; set; } = 100;
    
    public double DurationMin { get; set; } = 0.1;
    public double DurationMax { get; set; } = 300;
    
    public bool Break { get; set; }
}

public class EventConfig
{
    public Expression Intensity { get; set; }
    public Expression Duration { get; set; }
    
    public int EvaluateIntensity(dynamic dataValue)
    {
        int output = (int)EvaluateExpression(Intensity, dataValue);

        // TODO: add config for min and max variable (to avoid hardcoding default values)
        return Math.Clamp(output, 0, 100);
    }
    public double EvaluateDuration(dynamic dataValue)
    {
        double output = EvaluateExpression(Duration, dataValue);
        
        // TODO: add config for min and max variable (to avoid hardcoding default values)
        return Math.Clamp(output, 0, 300);
    }
    private double EvaluateExpression(Expression expression, dynamic dataValue)
    {
        expression.Parameters["value"] = dataValue switch
        {
            JObject jo => jo,
            JValue jv => jv,
            _ => dataValue
        };
        
        // TODO: throw user error if Expression cannot compute
        double result = Convert.ToDouble(expression.Evaluate());

        return result;
    }
    
    // print the full Json Event (with 'data') if one of it's name is detected
    public bool Print { get; set; }
    
    public Expression Condition { get; set; }

    public bool CheckCondition(dynamic dataValue)
    {
        if (Condition != null)
        {
            Condition.Parameters["value"] = dataValue switch
            {
                JObject jo => jo,
                JValue jv => jv,
                _ => dataValue
            };
            Condition.Parameters["intensity"] = EvaluateIntensity(dataValue);
            Condition.Parameters["duration"] = EvaluateDuration(dataValue);
        }

        return Condition == null || (bool)Condition.Evaluate()!;
    }
    
    public EventModifier[] Modifier { get; set; }

    // return number of modifier applied
    public int EvaluateWithAllModifiers(dynamic dataValue, out int newIntensity, out double newDuration)
    {
        newIntensity = EvaluateIntensity(dataValue);
        newDuration = EvaluateDuration(dataValue);
        
        if (Modifier == null) return 0;
            
        int modifierUsed = 0;
        foreach (EventModifier modifier in Modifier)
        {
            if (modifier.Intensity == null && modifier.Duration == null) continue;
            if (!modifier.CheckCondition(dataValue, newIntensity, newDuration)) continue;

            int tempIntensity = newIntensity;
            double tempDuration = newDuration;
            
            if (modifier.Intensity != null)
                tempIntensity = modifier.EvaluateIntensity(dataValue, newIntensity, newDuration);
            if (modifier.Duration != null)
                tempDuration = modifier.EvaluateDuration(dataValue, newIntensity, newDuration);
            
            newIntensity = tempIntensity;
            newDuration = tempDuration;
            
            modifierUsed++;
            
            if (modifier.Condition != null && modifier.Break)
                break;
        }
        return modifierUsed;
    }
}
public class GameConfig
{
    public string? Mode { get; set; }
    public Dictionary<String, EventConfig> Events { get; set; }
}

public class VibrationManager
{
    private BPGEView _bpgeView;
    
    public ButtplugClient Client;

    private ButtplugWebsocketConnector _connector;
    
    private Timer _timer;

    private int _gameId = 0;
    
    private static readonly int IntensitySize = 10 * 60 * 30;
    
    private int _counter = 0;

    private int[] _intensityArray = new int[IntensitySize];
    
    private int _currentVibration = 0;
    
    private Dictionary<string, EventConfig> _intensities = new();
    
    private static string GetFilePath(string fileName)
    {
        return Path.Combine(Directory.GetCurrentDirectory(), fileName);
    }
    
    private static GameConfig GetGameConfig(string fileName)
    {
        return new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new YamlConverterNCalcExpression())
            .Build()
            .Deserialize<GameConfig>(File.ReadAllText(GetFilePath(fileName)));
    } 
    
    private void ResetConfig()
    {
        _intensities = new Dictionary<string, EventConfig>();
    }
    
    private bool LoadGlobalConfig()
    {
        ResetConfig();
        GameConfig globalConfig = null;
        if(File.Exists(GetFilePath("global.yaml")))
        {
            _bpgeView.LogDebug("Loading file global.yaml");
            globalConfig = GetGameConfig("global.yaml");
        }
        if (File.Exists(GetFilePath("global.yml")))
        {
            _bpgeView.LogDebug("Loading file global.yml");
            globalConfig = GetGameConfig("global.yml");
        }
        if(globalConfig != null)
        {
            foreach (var (key, value) in globalConfig.Events)
            {
                _intensities.Add(key, value);
            }

            return true;
        }
        
        _bpgeView.LogInfo("No global config found, using empty config");
        return false;
    }
    
    private void LoadGameConfig(int gameId)
    {
        var exists = LoadGlobalConfig();
        GameConfig gameConfig = null;
        if(File.Exists(GetFilePath($"{gameId}.yaml")))
        {
            _bpgeView.LogDebug($"Loading file {gameId}.yaml");
            gameConfig = GetGameConfig($"{gameId}.yaml");
            
        }else if (File.Exists(GetFilePath($"{gameId}.yml")))
        {
            _bpgeView.LogDebug($"Loading file {gameId}.yml");
            gameConfig = GetGameConfig($"{gameId}.yml");
        }

        if (gameConfig != null)
        {
            switch (gameConfig.Mode)
            {
                case "override":
                    ResetConfig();
                    break;
                case "append":
                    break;
                default:
                    if (exists)
                    {
                        _bpgeView.LogInfo($"Unknown mode {gameConfig.Mode}, using global config");
                    }
                    else
                    {
                        _bpgeView.LogInfo($"Unknown mode {gameConfig.Mode}, global config does not exist, no events will be triggered");
                    }
                    return;
            }
            foreach (var (key, value) in gameConfig.Events)
            {
                _intensities.Add(key, value);
            }
        }else if(exists)
        {
            _bpgeView.LogInfo($"No config found for game {gameId}, using global config");
        }else
        {
            _bpgeView.LogInfo($"No config found for game {gameId}, global config does not exist, no events will be triggered");
        }
    }
    
    private static int CircularIndex(int index)
    {
        return index % IntensitySize;
    }

    private int CurrentIndex()
    {
        return CircularIndex(_counter);
    }
    
    public VibrationManager(BPGEView bpgeView)
    {
        _bpgeView = bpgeView;
    }
    
    public async void Init()
    {
        try
        {
            _timer = new Timer(100);
            Client = new ButtplugClient("BPGE");
            _connector = new ButtplugWebsocketConnector(new Uri($"ws://{_bpgeView.IntifaceIP}:{_bpgeView.IntifacePort}"));
            await Client.ConnectAsync(_connector);
            _timer.Elapsed += (sender, args) =>
            {
                if(_intensityArray[CurrentIndex()] != _currentVibration)
                {
                    var newIntensity = _intensityArray[CurrentIndex()];
                    _bpgeView.LogDebug($"Intensity changed from {_currentVibration} to {newIntensity}");
                    _currentVibration = newIntensity;
                    VibrateAll(_currentVibration);
                }
                _counter++;
            };
            Client.ServerDisconnect += (sender, args) =>
            {
                _bpgeView.LogInfo("Buttplug Client disconnected");
                _bpgeView.bpStatusLabel.Text = "Disconnected";
                _bpgeView.btnTest.Enabled = false;
            };
            Client.DeviceAdded += (sender, args) =>
            {
                _bpgeView.LogInfo($"Device added: {args.Device.Name}");
            };
            Client.DeviceRemoved += (sender, args) =>
            {
                _bpgeView.LogInfo($"Device removed: {args.Device.Name}");
            };
            _timer.Start();
            _bpgeView.LogInfo("Buttplug Client connected");
            _bpgeView.bpStatusLabel.Text = "Connected";
            _bpgeView.btnTest.Enabled = true;
            if (Client.Devices.Length == 0)
            {
                _bpgeView.LogInfo("No devices found, please manage your devices at Intiface\u00ae Central");
            }
            LoadGlobalConfig();
        }
        catch (Exception e)
        {
            _bpgeView.LogError($"Exception in BPManager: {Environment.NewLine}{e.ToString()}");
        }
    }
    
    public async void Reconnect()
    {
        await Client.DisconnectAsync();
        Init();
    }
    
    public void ProcessEvents(dynamic json)
    {
        foreach (dynamic item in json)
        {
            if (item.gameId != _gameId)
            {
                _bpgeView.LogInfo($"Game changed to {item.gameName} ({item.gameId})");
                _gameId = item.gameId;
                LoadGameConfig(_gameId);
                _bpgeView.LogDebug("Current config:");
                foreach (KeyValuePair<string, EventConfig> kvp in _intensities)
                {
                    _bpgeView.LogDebug($"Event: {kvp.Key} Intensity: {kvp.Value.Intensity.ExpressionString}% Duration: {kvp.Value.Duration.ExpressionString}s");
                }
            }
            if (item.type == "event")
            {
                foreach (dynamic ev in item.data.events)
                {
                    if (!_intensities.ContainsKey(ev.name.ToString())) continue;
                    EventConfig eventConfig = _intensities[ev.name.ToString()];

                    if (eventConfig.Print)
                        _bpgeView.LogInfo($"[PRINT] Event: {ev.name} {ev.data}");
                    
                    if (!eventConfig.CheckCondition(ev.data)) continue;
                    
                    int modifierUsed = eventConfig.EvaluateWithAllModifiers(ev.data, out int modifiedIntensity, out double modifiedDuration);
                    
                    _bpgeView.LogInfo($"Game: {item.gameName} Event: {ev.name} Intensity: {modifiedIntensity}% Duration: {modifiedDuration}s" + (modifierUsed > 0 ? $" Modifier Used: {modifierUsed}" : ""));
                    AddToArray(CurrentIndex(), modifiedIntensity, modifiedDuration);
                }
            }
        }
    }
    
    private void AddToArray(int index, int intensity, double duration)
    {
        if (duration == 0)
        {
            duration = 5 * 60;
        }
        if(intensity == 0)
        {
            ClearArray();
        } else
        {
            for (int i = index; i <= index+(duration * 10); i++)
            {
                if (_intensityArray[CircularIndex(i)] < intensity)
                {
                    _intensityArray[CircularIndex(i)] = intensity;
                }
            }
        }
    }
    
    public void ClearArray()
    {
        _intensityArray = new int[IntensitySize];
    }
    
    private void VibrateAll(int intensity)
    {
        if (Client.Connected)
        {
            foreach (var buttplugClientDevice in Client.Devices)
            {
                buttplugClientDevice.VibrateAsync((double)intensity/100);
            }    
        }
    }
}