﻿using FlightStreamDeck.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpDeck;
using SharpDeck.Events.Received;
using SharpDeck.Manifest;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FlightStreamDeck.Logics.Actions
{
    /// <summary>
    /// Note: We need to fix the JSON property names to avoid conversion to camel case
    /// </summary>
    public class GenericToggleSettings
    {
        [JsonProperty(nameof(Header))]
        public string Header { get; set; }
        [JsonProperty(nameof(ToggleValue))]
        public string ToggleValue { get; set; }
        [JsonProperty(nameof(ToggleValueData))]
        public string ToggleValueData { get; set; }
        [JsonProperty(nameof(FeedbackValue))]
        public string FeedbackValue { get; set; }
        [JsonProperty(nameof(DisplayValue))]
        public string DisplayValue { get; set; }
        [JsonProperty(nameof(DisplayValueUnit))]
        public string DisplayValueUnit { get; set; }
        [JsonProperty(nameof(DisplayValuePrecision))]
        public string DisplayValuePrecision { get; set; }
        [JsonProperty(nameof(ImageOn))]
        public string ImageOn { get; set; }
        [JsonProperty(nameof(ImageOn_base64))]
        public string ImageOn_base64 { get; set; }
        [JsonProperty(nameof(ImageOff))]
        public string ImageOff { get; set; }
        [JsonProperty(nameof(ImageOff_base64))]
        public string ImageOff_base64 { get; set; }
    }

    [StreamDeckAction("tech.flighttracker.streamdeck.generic.toggle")]
    public class GenericToggleAction : StreamDeckAction<GenericToggleSettings>
    {
        private readonly ILogger<GenericToggleAction> logger;
        private readonly IFlightConnector flightConnector;
        private readonly IImageLogic imageLogic;
        private readonly IEvaluator evaluator;
        private readonly EnumConverter enumConverter;

        private GenericToggleSettings settings = null;

        private TOGGLE_EVENT? toggleEvent = null;
        private uint? toggleEventDataUInt = null;
        private TOGGLE_VALUE? toggleEventDataVariable = null;
        private double? toggleEventDataVariableValue = null;

        private IEnumerable<TOGGLE_VALUE> feedbackVariables = new List<TOGGLE_VALUE>();
        private IExpression expression;
        private TOGGLE_VALUE? displayValue = null;

        private string customUnit = null;
        private int? customDecimals = null;

        private double? currentValue = null;
        private bool currentStatus = false;

        public GenericToggleAction(ILogger<GenericToggleAction> logger, IFlightConnector flightConnector, IImageLogic imageLogic,
            IEvaluator evaluator, EnumConverter enumConverter)
        {
            this.logger = logger;
            this.flightConnector = flightConnector;
            this.imageLogic = imageLogic;
            this.evaluator = evaluator;
            this.enumConverter = enumConverter;
        }

        protected override async Task OnWillAppear(ActionEventArgs<AppearancePayload> args)
        {
            var settings = args.Payload.GetSettings<GenericToggleSettings>();
            InitializeSettings(settings);

            flightConnector.GenericValuesUpdated += FlightConnector_GenericValuesUpdated;

            RegisterValues();

            await UpdateImage();
        }

        private void InitializeSettings(GenericToggleSettings settings)
        {
            this.settings = settings;

            TOGGLE_EVENT? newToggleEvent = enumConverter.GetEventEnum(settings.ToggleValue);
            (var newToggleEventDataUInt, var newToggleEventDataVariable) = enumConverter.GetUIntOrVariable(settings.ToggleValueData);

            (var newFeedbackVariables, var newExpression) = evaluator.Parse(settings.FeedbackValue);
            TOGGLE_VALUE? newDisplayValue = enumConverter.GetVariableEnum(settings.DisplayValue);

            if (int.TryParse(settings.DisplayValuePrecision, out int decimals))
            {
                customDecimals = decimals;
            }
            var newUnit = settings.DisplayValueUnit?.Trim();
            if (string.IsNullOrWhiteSpace(newUnit)) newUnit = null;

            if (!newFeedbackVariables.SequenceEqual(feedbackVariables) || newDisplayValue != displayValue
                || newUnit != customUnit
                || newToggleEventDataVariable != toggleEventDataVariable
                )
            {
                DeRegisterValues();
            }

            toggleEvent = newToggleEvent;
            feedbackVariables = newFeedbackVariables;
            expression = newExpression;
            displayValue = newDisplayValue;
            customUnit = newUnit;
            toggleEventDataUInt = newToggleEventDataUInt;
            toggleEventDataVariable = newToggleEventDataVariable;

            RegisterValues();
        }

        private async void FlightConnector_GenericValuesUpdated(object sender, ToggleValueUpdatedEventArgs e)
        {
            if (StreamDeck == null) return;

            var valuesWithDefaultUnits = e.GenericValueStatus.Where(o => o.Key.unit == null).ToDictionary(o => o.Key.variable, o => o.Value);
            var newStatus = expression != null && evaluator.Evaluate(valuesWithDefaultUnits, expression);
            var isUpdated = newStatus != currentStatus;
            currentStatus = newStatus;

            if (displayValue.HasValue && e.GenericValueStatus.ContainsKey((displayValue.Value, customUnit)))
            {
                var newValue = e.GenericValueStatus[(displayValue.Value, customUnit)];
                isUpdated |= newValue != currentValue;
                currentValue = newValue;
            }

            if (toggleEventDataVariable.HasValue && e.GenericValueStatus.ContainsKey((toggleEventDataVariable.Value, null)))
            {
                toggleEventDataVariableValue = e.GenericValueStatus[(toggleEventDataVariable.Value, null)];
            }

            if (isUpdated)
            {
                await UpdateImage();
            }
        }

        protected override Task OnWillDisappear(ActionEventArgs<AppearancePayload> args)
        {
            flightConnector.GenericValuesUpdated -= FlightConnector_GenericValuesUpdated;
            DeRegisterValues();
            return Task.CompletedTask;
        }

        protected override async Task OnSendToPlugin(ActionEventArgs<JObject> args)
        {
            if (args.Payload.TryGetValue("convertToEmbed", out JToken fileKeyObject))
            {
                var fileKey = fileKeyObject.Value<string>();
                await ConvertLinkToEmbed(fileKey);
            }
            else if (args.Payload.TryGetValue("convertToLink", out fileKeyObject))
            {
                var fileKey = fileKeyObject.Value<string>();

                System.Windows.Application.Current.Dispatcher.Invoke(() => ConvertEmbedToLink(fileKey));
            }
            else
            {
                InitializeSettings(args.Payload.ToObject<GenericToggleSettings>());
            }
            await UpdateImage();
        }

        private async Task ConvertLinkToEmbed(string fileKey)
        {
            switch (fileKey)
            {
                case "ImageOn":
                    settings.ImageOn_base64 = Convert.ToBase64String(File.ReadAllBytes(settings.ImageOn));
                    break;
                case "ImageOff":
                    settings.ImageOff_base64 = Convert.ToBase64String(File.ReadAllBytes(settings.ImageOff));
                    break;
            }

            await SetSettingsAsync(settings);
            await SendToPropertyInspectorAsync(new
            {
                Action = "refresh",
                Settings = settings
            });
            InitializeSettings(settings);
        }

        private async Task ConvertEmbedToLink(string fileKey)
        {
            var dialog = new SaveFileDialog
            {
                FileName = fileKey switch
                {
                    "ImageOn" => Path.GetFileName(settings.ImageOn),
                    "ImageOff" => Path.GetFileName(settings.ImageOff),
                    _ => "image.png"
                },
                Filter = "Images|*.jpg;*.jpeg;*.png"
            };
            if (dialog.ShowDialog() == true)
            {
                var bytes = fileKey switch
                {
                    "ImageOn" => Convert.FromBase64String(settings.ImageOn_base64),
                    "ImageOff" => Convert.FromBase64String(settings.ImageOff_base64),
                    _ => null
                };
                if (bytes != null)
                {
                    File.WriteAllBytes(dialog.FileName, bytes);
                }
                switch (fileKey)
                {
                    case "ImageOn":
                        settings.ImageOn_base64 = null;
                        settings.ImageOn = dialog.FileName.Replace("\\", "/");
                        break;
                    case "ImageOff":
                        settings.ImageOff_base64 = null;
                        settings.ImageOff = dialog.FileName.Replace("\\", "/");
                        break;
                }
            }

            await SetSettingsAsync(settings);
            await SendToPropertyInspectorAsync(new
            {
                Action = "refresh",
                Settings = settings
            });
            InitializeSettings(settings);
        }

        private void RegisterValues()
        {
            if (toggleEvent.HasValue) flightConnector.RegisterToggleEvent(toggleEvent.Value);

            var values = new List<(TOGGLE_VALUE variables, string unit)>();
            foreach (var feedbackVariable in feedbackVariables) values.Add((feedbackVariable, null));
            if (displayValue.HasValue) values.Add((displayValue.Value, customUnit));
            if (toggleEventDataVariable.HasValue) values.Add((toggleEventDataVariable.Value, null));

            if (values.Count > 0)
            {
                flightConnector.RegisterSimValues(values.ToArray());
            }
        }

        private void DeRegisterValues()
        {
            var values = new List<(TOGGLE_VALUE variables, string unit)>();
            foreach (var feedbackVariable in feedbackVariables) values.Add((feedbackVariable, null));
            if (displayValue.HasValue) values.Add((displayValue.Value, customUnit));
            if (toggleEventDataVariable.HasValue) values.Add((toggleEventDataVariable.Value, null));

            if (values.Count > 0)
            {
                flightConnector.DeRegisterSimValues(values.ToArray());
            }

            currentValue = null;
            toggleEventDataVariableValue = null;
        }

        protected override Task OnKeyDown(ActionEventArgs<KeyPayload> args)
        {
            if (toggleEvent.HasValue)
            {
                if (!(toggleEventDataVariable is null) && toggleEventDataVariableValue.HasValue)
                {
                    uint parameterForSimConnect = Convert.ToUInt32(Math.Round(toggleEventDataVariableValue.Value));
                    flightConnector.Trigger(toggleEvent.Value, parameterForSimConnect);
                }
                else
                {
                    flightConnector.Trigger(toggleEvent.Value, toggleEventDataUInt ?? 0);
                }
            }
            return Task.CompletedTask;
        }

        private async Task UpdateImage()
        {
            if (settings != null)
            {
                byte[] imageOnBytes = null;
                byte[] imageOffBytes = null;
                if (settings.ImageOn_base64 != null)
                {
                    var s = settings.ImageOn_base64;
                    s = s.Replace('-', '+').Replace('_', '/').PadRight(4 * ((s.Length + 3) / 4), '=');
                    imageOnBytes = Convert.FromBase64String(s);
                }
                if (settings.ImageOff_base64 != null)
                {
                    var s = settings.ImageOff_base64;
                    s = s.Replace('-', '+').Replace('_', '/').PadRight(4 * ((s.Length + 3) / 4), '=');
                    imageOffBytes = Convert.FromBase64String(s);
                }
                await SetImageAsync(imageLogic.GetImage(settings.Header, currentStatus,
                    value: (displayValue.HasValue && currentValue.HasValue) ? currentValue.Value.ToString("F" + EventValueLibrary.GetDecimals(displayValue.Value, customDecimals)) : "",
                    imageOnFilePath: settings.ImageOn, imageOnBytes: imageOnBytes,
                    imageOffFilePath: settings.ImageOff, imageOffBytes: imageOffBytes));
            }
        }
    }
}
