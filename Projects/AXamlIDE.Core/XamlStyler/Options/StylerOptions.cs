// (c) Xavalon. All rights reserved.
#nullable disable // Upstream XAML Styler code predates nullable reference types.

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AXamlIDE.XamlStyler.DocumentManipulation;

namespace AXamlIDE.XamlStyler.Options
{
    public class StylerOptions : IStylerOptions
    {
        private const string DefaultOptionsPath = "AXamlIDE.XamlStyler.Options.DefaultSettings.json";
        private const int FallbackIndentSize = 4;

        public StylerOptions()
        {
            this.InitializeProperties();
        }

        /// <summary>
        /// Constructor that accepts an external configuration path before initializing settings.
        /// </summary>
        /// <param name="config">Path to external configuration file.</param>
        public StylerOptions(string config = "")
        {
            if (!String.IsNullOrWhiteSpace(config) && File.Exists(config))
            {
                this.ConfigPath = config;
            }

            this.InitializeProperties();
        }

        /// <summary>
        /// Constructor that skips loading any configuration (used by <see cref="Deserialize"/>).
        /// </summary>
        /// <param name="isJsonConstructor">Dummy parameter to differentiate from default constructor.</param>
        [SuppressMessage("Usage", "CA1801:Review unused parameters", Justification = "Required for deserialization", MessageId = "isJsonConstructor")]
        [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Required for deserialization")]
        public StylerOptions(bool isJsonConstructor = true) { }

        // Indentation
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [DefaultValue(-1)]
        [JsonPropertyName("IndentSize")]
        [Browsable(false)]
        public int IndentSize { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [DefaultValue(true)]
        [Browsable(false)]
        [JsonIgnore]
        public bool UseVisualStudioIndentSize { get; private set; } = true;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [DefaultValue(null)]
        [JsonPropertyName("IndentWithTabs")]
        [Browsable(false)]
        public bool? IndentWithTabs { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [DefaultValue(true)]
        [Browsable(false)]
        [JsonIgnore]
        public bool UseVisualStudioIndentWithTabs { get; private set; } = true;

        // Attribute formatting
        [Category("Attribute Formatting")]
        [DisplayName("Attribute tolerance")]
        [JsonPropertyName("AttributesTolerance")]
        [Description("Defines the maximum number of attributes allowed on a single line. If the number of attributes exceeds this value, XAML Styler will break the attributes up across multiple lines. A value of less than 1 means always break up the attributes.\r\n\r\nDefault Value: 2")]
        [DefaultValue(2)]
        public int AttributesTolerance { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Keep first attribute on same line")]
        [JsonPropertyName("KeepFirstAttributeOnSameLine")]
        [Description("Defines whether the first line of attribute(s) should appear on the same line as the element's start tag.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool KeepFirstAttributeOnSameLine { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Max attribute characters per line")]
        [JsonPropertyName("MaxAttributeCharactersPerLine")]
        [Description("Defines the maximum character length of attributes an element can have on each line after the start tag (not including indentation characters). A value of less than 1 means no limit. Note: This setting only takes effect if Max Attributes Per Line is greater than 1 and Attribute Tolerance is greater than 2.\r\n\r\nDefault Value: 0")]
        [DefaultValue(0)]
        public int MaxAttributeCharactersPerLine { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Max attributes per line")]
        [JsonPropertyName("MaxAttributesPerLine")]
        [Description("Defines the maximum number of attributes an element can have on each line after the start tag if the number of attributes exceeds the attribute tolerance. A value of less than 1 means no limit.\r\n\r\nDefault Value: 1")]
        [DefaultValue(1)]
        public int MaxAttributesPerLine { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Newline exemption elements")]
        [JsonPropertyName("NewlineExemptionElements")]
        [Description("Defines a list of elements whose attributes should not be broken across lines.\r\n\r\nDefault Value: RadialGradientBrush, GradientStop, LinearGradientBrush, ScaleTransform, SkewTransform, RotateTransform, TranslateTransform, Trigger, Setter")]
        [DefaultValue("RadialGradientBrush, GradientStop, LinearGradientBrush, ScaleTransform, SkewTransform, RotateTransform, TranslateTransform, Trigger, Condition, Setter")]
        public string NoNewLineElements { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Separate by groups")]
        [JsonPropertyName("SeparateByGroups")]
        [Description("Defines whether attributes belonging to different rule groups should be put on separate lines, while, if possible, keeping attributes in the same group on the same line.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool PutAttributeOrderRuleGroupsOnSeparateLines { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Attribute indentation")]
        [JsonPropertyName("AttributeIndentation")]
        [Description("Defines the number of spaces that attributes are indented on elements with more than one line of attributes. A value of 0 will align indentation with the first attribute.\r\n\r\nDefault Value: 0")]
        [DefaultValue(0)]
        public int AttributeIndentation { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Attribute indentation style")]
        [JsonPropertyName("AttributeIndentationStyle")]
        [Description("Defines how attributes are indented.\r\n\r\nDefault Value: Spaces")]
        [DefaultValue(AttributeIndentationStyle.Spaces)]
        public AttributeIndentationStyle AttributeIndentationStyle { get; set; }

        [Category("Attribute Formatting")]
        [DisplayName("Remove design-time references")]
        [JsonPropertyName("RemoveDesignTimeReferences")]
        [Description("Defines whether design-time references automatically added to new pages and controls should be removed.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool RemoveDesignTimeReferences { get; set; }

        // Attribute Reordering
        [Category("Attribute Reordering")]
        [DisplayName("Enable Attribute Reordering")]
        [JsonPropertyName("EnableAttributeReordering")]
        [Description("Defines whether attributes should be reordered. If false, attributes will not be reordered in any way.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool EnableAttributeReordering { get; set; }

        [Category("Attribute Reordering")]
        [DisplayName("Attribute ordering rule groups")]
        [JsonPropertyName("AttributeOrderingRuleGroups")]
        [Description("Defines attribute ordering rule groups. Each string element is one group. Use ',' as a delimiter between attributes. 'DOS' wildcards are allowed. XAML Styler will order attributes in groups from top to bottom, and within groups left to right.")]
        [DefaultValue(new[]
        {
            "x:Class",
            "xmlns, xmlns:x",
            "xmlns:*",
            "x:Key, Key, x:Name, Name, x:Uid, Uid, Title",
            "Grid.Row, Grid.RowSpan, Grid.Column, Grid.ColumnSpan, Canvas.Left, Canvas.Top, Canvas.Right, Canvas.Bottom",
            "Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight",
            "Margin, Padding, HorizontalAlignment, VerticalAlignment, HorizontalContentAlignment, VerticalContentAlignment, Panel.ZIndex",
            "*:*, *",
            "PageSource, PageIndex, Offset, Color, TargetName, Property, Value, StartPoint, EndPoint",
            "mc:Ignorable, d:IsDataSource, d:LayoutOverrides, d:IsStaticText",
            //Storyboards, fixes #30
            "Storyboard.*, From, To, Duration",
        })]
        [TypeConverter(typeof(StringArrayConverter))]
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Required for serialization/deserialization")]
        public string[] AttributeOrderingRuleGroups { get; set; }

        [Category("Attribute Reordering")]
        [DisplayName("First-line attributes")]
        [JsonPropertyName("FirstLineAttributes")]
        [Description("Defines a list of attributes which should always appear on the same line as the element's start tag. Attribute reordering must be enabled for this setting to take effect.\r\n\r\nDefault Value: None")]
        [DefaultValue("")]
        public string FirstLineAttributes { get; set; }

        [Category("Attribute Reordering")]
        [DisplayName("Order attributes by name")]
        [JsonPropertyName("OrderAttributesByName")]
        [Description("Defines whether attributes should be ordered by name if not determined by a rule.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool OrderAttributesByName { get; set; }

        // Element formatting
        [Category("Element Formatting")]
        [DisplayName("Put ending brackets on new line")]
        [JsonPropertyName("PutEndingBracketOnNewLine")]
        [Description("Defines whether to put ending brackets on a new line.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool PutEndingBracketOnNewLine { get; set; }

        [Category("Element Formatting")]
        [DisplayName("Remove ending tag of empty elements")]
        [JsonPropertyName("RemoveEndingTagOfEmptyElement")]
        [Description("Defines whether to remove the end tag of an empty element.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool RemoveEndingTagOfEmptyElement { get; set; }

        [Category("Element Formatting")]
        [DisplayName("Space before ending slash in self-closing elements")]
        [JsonPropertyName("SpaceBeforeClosingSlash")]
        [Description("Defines whether there should be a space before the slash in ending brackets for self-closing elements.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool SpaceBeforeClosingSlash { get; set; }

        [Category("Element Formatting")]
        [DisplayName("Root element line breaks between attributes")]
        [JsonPropertyName("RootElementLineBreakRule")]
        [Description("Defines whether attributes of the document root element are broken into multiple lines.\r\n\r\nDefault Value: Default (use same rules as other elements)")]
        [DefaultValue(LineBreakRule.Default)]
        public LineBreakRule RootElementLineBreakRule { get; set; }

        // Element reordering
        [Category("Element Reordering")]
        [DisplayName("Reorder visual state manager")]
        [JsonPropertyName("ReorderVSM")]
        [Description("Defines whether to reorder the visual state manager. When set to first or last, the visual state manager will be moved to the first or last child element in its parent, respectively, otherwise it will not be moved.\r\n\r\nDefault Value: Last")]
        [DefaultValue(VisualStateManagerRule.Last)]
        public VisualStateManagerRule ReorderVSM { get; set; }

        [Category("Element Reordering")]
        [DisplayName("Reorder grid panel children")]
        [JsonPropertyName("ReorderGridChildren")]
        [Description("Defines whether to reorder the children of a Grid by row/column. When true, children will be reordered in an ascending fashion by looking first at Grid.Row, then by Grid.Column.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool ReorderGridChildren { get; set; }

        [Category("Element Reordering")]
        [DisplayName("Reorder canvas panel children")]
        [JsonPropertyName("ReorderCanvasChildren")]
        [Description("Defines whether to reorder the children of a Canvas by left/top/right/bottom. When true, children will be reordered in an ascending fashion by first at Canvas.Left, then by Canvas.Top, Canvas.Right, and finally, Canvas.Bottom.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool ReorderCanvasChildren { get; set; }

        [Category("Element Reordering")]
        [DisplayName("Reorder setters")]
        [JsonPropertyName("ReorderSetters")]
        [Description("Defines whether to reorder 'Setter' elements in style/trigger elements. When this is set, children will be reordered in an ascending fashion by looking at their Property and/or TargetName properties.\r\n\r\nDefault Value: None")]
        [DefaultValue(ReorderSettersBy.None)]
        public ReorderSettersBy ReorderSetters { get; set; }

        // Markup Extension
        [Category("Markup Extension")]
        [DisplayName("Enable markup extension formatting")]
        [JsonPropertyName("FormatMarkupExtension")]
        [Description("Defines whether to format Markup Extensions (attributes containing '{}'). When true, attributes with markup extensions will always be put on a new line, unless the element is under the attribute tolerance or one of the specified elements is in the list of elements with no line breaks between attributes.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool FormatMarkupExtension { get; set; }

        [Category("Markup Extension")]
        [DisplayName("Keep markup extensions of these types on one line")]
        [JsonPropertyName("NoNewLineMarkupExtensions")]
        [Description("Defines a comma-separated list of Markup Extensions that are always kept on a single line\r\n\r\nDefault Value: x:Bind, Binding")]
        [DefaultValue("x:Bind, Binding")]
        public string NoNewLineMarkupExtensions { get; set; }

        // Thickness formatting
        [Category("Thickness formatting")]
        [DisplayName("Thickness separator")]
        [JsonPropertyName("ThicknessSeparator")]
        [Description("Defines how thickness attributes (i.e., margin, padding, etc.) should be formatted.\r\n\r\nDefault Value: Comma")]
        [DefaultValue(ThicknessStyle.Comma)]
        public ThicknessStyle ThicknessStyle { get; set; }

        [Category("Thickness formatting")]
        [DisplayName("Thickness attributes")]
        [JsonPropertyName("ThicknessAttributes")]
        [Description("Defines a list of attributes that get reformatted if content appears to be a thickness.\r\n\r\nDefault Value: Margin, Padding, BorderThickness, ThumbnailClipMargin")]
        [DefaultValue("Margin, Padding, BorderThickness, ThumbnailClipMargin")]
        public string ThicknessAttributes { get; set; }

        // Misc
        [Category("Misc")]
        [DisplayName("Format XAML on save")]
        [JsonPropertyName("FormatOnSave")]
        [Description("Defines whether to automatically format the active XAML document while saving.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool FormatOnSave { get; set; }

        [Category("Misc")]
        [DisplayName("Automatically save and close documents opened by XAML Styler")]
        [JsonPropertyName("SaveAndCloseOnFormat")]
        [Description("When set to true, XAML Styler will automatically save and close any files it opened when formatting.\r\n\r\nDefault Value: true")]
        [DefaultValue(true)]
        public bool SaveAndCloseOnFormat { get; set; }

        [Category("Misc")]
        [DisplayName("Comment padding")]
        [JsonPropertyName("CommentPadding")]
        [Description("Determines the number of spaces a XAML comment should be padded with.\r\n\r\nDefault Value: 2")]
        [DefaultValue(2)]
        public int CommentSpaces { get; set; }

        // Configuration

        [Category("XAML Styler Configuration")]
        [RefreshProperties(RefreshProperties.All)]
        [DisplayName("Search to drives root")]
        [Description("When set to true, XAML Styler will look for an external XAML Styler configuration file not only up through your solution directory, but up through the drives root of the current solution so you can share one configuration file through multiple solutions.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        [JsonIgnore]
        public bool SearchToDriveRoot { get; set; }

        [Category("Attribute Reordering")]
        [DisplayName("Ignore Design-Time Reference Prefix")]
        [JsonPropertyName("IgnoreDesignTimeReferencePrefix")]
        [Description("Defines whether attributes with the design-time reference namespace prefix are ignored when ordering.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        public bool IgnoreDesignTimeReferencePrefix { get; set; }

        private bool resetToDefault;

        [Category("XAML Styler Configuration")]
        [RefreshProperties(RefreshProperties.All)]
        [DisplayName("Reset to default")]
        [Description("When set to true, all XAML Styler settings will be reset to their defaults.\r\n\r\nDefault Value: false")]
        [DefaultValue(false)]
        [JsonIgnore]
        public bool ResetToDefault
        {
            get => this.resetToDefault;
            set
            {
                this.resetToDefault = value;

                if (this.resetToDefault)
                {
                    this.ConfigPath = String.Empty;
                    this.InitializeProperties();
                }
            }
        }

        private string configPath = String.Empty;

        [Category("XAML Styler Configuration")]
        [RefreshProperties(RefreshProperties.All)]
        [DisplayName("External configuration file")]
        [Description("Defines location of external XAML Styler configuration file. Specifying an external configuration file allows you to easily point multiple instances to a shared configuration. The configuration path can be local or network-based. Invalid configurations will be ignored.\r\n\r\nDefault Value: N/A")]
        [DefaultValue("")]
        [JsonIgnore]
        public string ConfigPath
        {
            get => this.configPath;
            set
            {
                this.configPath = value;

                if (!String.IsNullOrEmpty(value))
                {
                    this.TryLoadExternalConfiguration();
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [JsonPropertyName("SuppressProcessing")]
        [DefaultValue(false)]
        [Browsable(false)]
        public bool SuppressProcessing { get; set; }

        /// <summary>
        /// Creates a clone from the current instance.
        /// </summary>
        /// <returns>A clone from the current instance.</returns>
        public IStylerOptions Clone()
        {
            var clone = (StylerOptions)this.MemberwiseClone();
            clone.AttributeOrderingRuleGroups = (string[])this.AttributeOrderingRuleGroups?.Clone();

            return clone;
        }

        // How Newtonsoft read settings files, so existing Settings.XamlStyler files keep working:
        // property names in any case, comments, trailing commas, numbers in quotes, enums by name or number.
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly JsonDocumentOptions JsonDocumentOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Reads a settings file the way Newtonsoft's DefaultValueHandling.IgnoreAndPopulate did:
        /// a setting missing from the JSON gets its [DefaultValue]; the rest are read by their
        /// [JsonPropertyName]. Returns null when the JSON isn't an object. Throws on invalid JSON.
        /// </summary>
        private static StylerOptions Deserialize(string config)
        {
            using (JsonDocument document = JsonDocument.Parse(config, StylerOptions.JsonDocumentOptions))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var options = new StylerOptions(isJsonConstructor: true);
                options.LoadFallbackConfiguration();

                foreach (PropertyInfo property in typeof(StylerOptions).GetProperties())
                {
                    string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                    if (name != null && StylerOptions.TryGetSetting(document.RootElement, name, out JsonElement value))
                    {
                        property.SetValue(options, value.Deserialize(property.PropertyType, StylerOptions.JsonOptions));
                    }
                }

                return options;
            }
        }

        private static bool TryGetSetting(JsonElement settings, string name, out JsonElement value)
        {
            foreach (JsonProperty setting in settings.EnumerateObject())
            {
                if (String.Equals(setting.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = setting.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private void InitializeProperties()
        {
            if (!this.TryLoadExternalConfiguration())
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(StylerOptions.DefaultOptionsPath))
                using (var reader = new StreamReader(stream))
                {
                    this.LoadConfiguration(reader.ReadToEnd());
                }
            }
        }

        private bool TryLoadExternalConfiguration()
        {
            return String.IsNullOrWhiteSpace(this.ConfigPath) || !File.Exists(this.ConfigPath)
                ? false
                : this.LoadConfiguration(File.ReadAllText(this.ConfigPath));
        }

        private bool LoadConfiguration(string config)
        {
            try
            {
                StylerOptions configOptions = StylerOptions.Deserialize(config);

                if (configOptions == null)
                {
                    this.LoadFallbackConfiguration();
                }
                else
                {
                    foreach (PropertyDescriptor propertyDescriptor in TypeDescriptor.GetProperties(this))
                    {
                        // Cannot set Config Path from External Configuration.
                        if (propertyDescriptor.Name.Equals(nameof(this.ConfigPath), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // If a valid IndentSize is specified in configuration, do not load VS settings.
                        if (propertyDescriptor.Name.Equals(nameof(this.IndentSize), StringComparison.Ordinal))
                        {
                            int indentSize;
                            try
                            {
                                indentSize = Convert.ToInt32(propertyDescriptor.GetValue(configOptions), CultureInfo.InvariantCulture);
                            }
                            catch (Exception)
                            {
                                indentSize = -1;
                            }


                            // Cannot specify MissingMemberHandling for a single property, so relying on JSON default
                            // value to detect missing member, and setting default on detection.
                            if (indentSize > 0)
                            {
                                this.IndentSize = indentSize;
                                this.UseVisualStudioIndentSize = false;
                            }
                            else
                            {
                                this.IndentSize = StylerOptions.FallbackIndentSize;
                            }
                        }
                        // If a valid IndentWithTabs is specified in configuration, do not load VS settings.
                        // If it is unspecified, it will be null.
                        else if (propertyDescriptor.Name.Equals(nameof(this.IndentWithTabs), StringComparison.Ordinal) &&
                            propertyDescriptor.GetValue(configOptions) != null)
                        {
                            bool? indentWithTabs;
                            try
                            {
                                indentWithTabs = Convert.ToBoolean(propertyDescriptor.GetValue(configOptions), CultureInfo.InvariantCulture);
                            }
                            catch (Exception)
                            {
                                indentWithTabs = null;
                            }

                            if (indentWithTabs == true)
                            {
                                this.IndentWithTabs = true;
                                this.UseVisualStudioIndentWithTabs = false;
                            }
                            else
                            {
                                this.IndentWithTabs = indentWithTabs;
                            }
                        }
                        else
                        {
                            propertyDescriptor.SetValue(this, propertyDescriptor.GetValue(configOptions));
                        }
                    }
                }
            }
            catch (Exception)
            {
                this.LoadFallbackConfiguration();
                return false;
            }

            return true;
        }

        private void LoadFallbackConfiguration()
        {
            // Initialize all properties with "DefaultValueAttrbute" to their default value
            foreach (PropertyDescriptor propertyDescriptor in TypeDescriptor.GetProperties(this))
            {
                // Set default value if DefaultValueAttribute is present
                if (propertyDescriptor.Attributes[typeof(DefaultValueAttribute)] is DefaultValueAttribute attribute)
                {
                    propertyDescriptor.SetValue(this, attribute.Value);
                }
            }
        }
    }
}