using HomeLink.Models;

namespace HomeLink.Services;

/// <summary>
/// Service for generating human-readable location descriptions with friendly, varied phrases
/// </summary>
public static class HumanReadableService
{
    private static readonly Random Random = new();

    #region Public Methods

    /// <summary>
    /// Creates a human-readable location text from LocationInfo with full context including movement.
    /// </summary>
    public static string CreateHumanReadableText(LocationInfo location)
    {
        string baseText = CreateBaseLocationText(location);
        string movementContext = GetMovementContext(location.Velocity);
        
        if (!string.IsNullOrEmpty(movementContext))
        {
            return $"{movementContext} {baseText.ToLowerInvariant()}";
        }
        
        return baseText;
    }

    /// <summary>
    /// Creates a human-readable location text from Nominatim address.
    /// </summary>
    public static string CreateHumanReadableText(NominatimAddress? address, LocationInfo? location = null)
    {
        string baseText = CreateBaseLocationText(address);
        
        // If we have location metadata, add movement context
        if (location != null)
        {
            string movementContext = GetMovementContext(location.Velocity);
            if (!string.IsNullOrEmpty(movementContext))
            {
                return $"{movementContext} {baseText.ToLowerInvariant()}";
            }
        }
        
        return baseText;
    }

    /// <summary>
    /// Creates human-readable text for a known location, adding movement context if applicable.
    /// </summary>
    public static string CreateHumanReadableTextForKnownLocation(LocationInfo location)
    {
        KnownLocation? knownLocation = location.MatchedKnownLocation;
        if (knownLocation == null)
            return CreateHumanReadableText(location);
        
        // If moving at significant speed, they might be leaving/arriving
        if (location.Velocity is > 5)
        {
            // Moving while at a known location - likely arriving or leaving
            if (location.Velocity.Value > 30)
            {
                return PickRandom(
                    $"Passing by {knownLocation.DisplayText}",
                    $"Zooming past {knownLocation.DisplayText}",
                    $"Whizzing by {knownLocation.DisplayText}");
            }
            return PickRandom(
                $"Near {knownLocation.DisplayText}",
                $"Around {knownLocation.DisplayText}",
                $"By {knownLocation.DisplayText}");
        }
        
        // Stationary at known location - use varied phrases
        return PickRandom(
            $"At {knownLocation.DisplayText}",
            $"Currently at {knownLocation.DisplayText}",
            $"Chilling at {knownLocation.DisplayText}",
            $"Hanging out at {knownLocation.DisplayText}",
            $"Found at {knownLocation.DisplayText}");
    }

    #endregion

    #region Movement Context

    /// <summary>
    /// Gets a descriptive movement prefix based on velocity with variety.
    /// </summary>
    private static string GetMovementContext(int? velocityKmh)
    {
        if (!velocityKmh.HasValue || velocityKmh.Value < 2)
            return string.Empty; // Stationary or GPS drift
        
        return velocityKmh.Value switch
        {
            < 6 => PickRandom(WalkingPhrases),           // 2-5 km/h - walking pace
            < 15 => PickRandom(StrollingPhrases),        // 6-14 km/h - brisk walk or slow bike
            < 50 => PickRandom(CityTravelPhrases),       // 15-49 km/h - city driving
            < 90 => PickRandom(DrivingPhrases),          // 50-89 km/h - highway driving
            < 150 => PickRandom(HighwayPhrases),         // 90-149 km/h - fast highway
            < 300 => PickRandom(TrainPhrases),           // 150-299 km/h - high-speed train
            _ => PickRandom(FlyingPhrases)               // 300+ km/h - airplane
        };
    }

    private static readonly string[] WalkingPhrases =
    [
        "Taking a stroll",
        "Walking",
        "On foot",
        "Out for a walk",
        "Wandering",
        "Exploring on foot",
        "Having a Spaziergang"
    ];

    private static readonly string[] StrollingPhrases =
    [
        "Briskly walking",
        "Strolling along",
        "On the move",
        "Making my way",
        "Hurrying a bit",
        "Power walking"
    ];

    private static readonly string[] CityTravelPhrases =
    [
        "Traveling through",
        "Making my way through",
        "Passing through",
        "On the road",
        "In transit",
        "Cruising through"
    ];

    private static readonly string[] DrivingPhrases =
    [
        "Driving",
        "On the highway",
        "Cruising along",
        "On the road",
        "Making good time",
        "Motoring along"
    ];

    private static readonly string[] HighwayPhrases =
    [
        "Speeding along",
        "Flying down the road",
        "Making great time",
        "On the Autobahn",
        "Zooming through"
    ];

    private static readonly string[] TrainPhrases =
    [
        "On a train",
        "Riding the rails",
        "On the ÖBB",
        "Taking the train",
        "Zug fahren",
        "On the Railjet"
    ];

    private static readonly string[] FlyingPhrases =
    [
        "Flying",
        "Up in the air",
        "In the clouds",
        "Taking flight",
        "Soaring above"
    ];

    #endregion

    #region Base Location Text

    /// <summary>
    /// Creates the base location text from LocationInfo.
    /// </summary>
    private static string CreateBaseLocationText(LocationInfo location)
    {
        // Try to build location from available properties
        string? locality = location.City ?? location.Town ?? location.Village;
        string? district = location.District;
        string? country = location.Country;

        // Check if it's Vienna based on stored properties
        if (IsViennaCity(locality))
        {
            return CreateViennaLocationText(district);
        }

        return CreateGenericLocationText(district, locality, null, country);
    }

    /// <summary>
    /// Creates the base location text from NominatimAddress.
    /// </summary>
    private static string CreateBaseLocationText(NominatimAddress? address)
    {
        if (address == null)
            return PickRandom("Somewhere in the world", "Location unknown", "Off the grid");

        // Check if it's Vienna (Wien) - handle district format
        if (IsVienna(address))
        {
            string? districtName = address.Suburb ?? address.CityDistrict ?? address.District;
            return CreateViennaLocationText(districtName);
        }

        // Check for Austrian location
        if (IsAustria(address.Country))
        {
            return CreateAustrianLocationText(address);
        }

        // Get locality and district
        string? genericLocality = address.City ?? address.Town ?? address.Village ?? address.Municipality;
        string? genericDistrict = address.Suburb ?? address.CityDistrict ?? address.District;
        
        return CreateGenericLocationText(genericDistrict, genericLocality, address.State, address.Country);
    }

    /// <summary>
    /// Creates location text for Austrian locations outside Vienna.
    /// </summary>
    private static string CreateAustrianLocationText(NominatimAddress address)
    {
        string? locality = address.City ?? address.Town ?? address.Village ?? address.Municipality;
        string? district = address.Suburb ?? address.CityDistrict ?? address.District;
        string? state = address.State;

        // Check for well-known Austrian cities
        if (!string.IsNullOrEmpty(locality))
        {
            string? cityVibe = GetAustrianCityVibe(locality);
            if (!string.IsNullOrEmpty(cityVibe))
            {
                return PickRandom(
                    $"In {locality}",
                    $"Visiting {locality}",
                    $"Currently in {locality}",
                    $"In {locality} – {cityVibe}");
            }
        }

        // Build standard Austrian location text with variety
        List<string> parts = new();
        if (!string.IsNullOrEmpty(district) && district != locality)
            parts.Add(district);
        if (!string.IsNullOrEmpty(locality))
            parts.Add(locality);

        string locationPart = parts.Count > 0 ? string.Join(", ", parts) : "";

        // Add Austrian state context
        string? stateVibe = GetAustrianStateVibe(state);
        
        if (!string.IsNullOrEmpty(locationPart))
        {
            if (!string.IsNullOrEmpty(stateVibe))
            {
                return PickRandom(
                    $"In {locationPart}, Austria",
                    $"Currently in {locationPart}",
                    $"In {locationPart} – {stateVibe}");
            }
            return PickRandom(
                $"In {locationPart}, Austria",
                $"Somewhere in {locationPart}",
                $"Currently in {locationPart}");
        }

        // Fallback to state
        if (!string.IsNullOrEmpty(state))
        {
            if (!string.IsNullOrEmpty(stateVibe))
            {
                return PickRandom(
                    $"Somewhere in {state}",
                    $"In {state} – {stateVibe}",
                    $"Exploring {state}");
            }
            return PickRandom(
                $"Somewhere in {state}",
                $"In {state}, Austria",
                $"Roaming around {state}");
        }

        return PickRandom(AustriaGenericPhrases);
    }

    /// <summary>
    /// Gets a fun vibe for well-known Austrian cities.
    /// </summary>
    private static string? GetAustrianCityVibe(string city)
    {
        return city.ToLowerInvariant() switch
        {
            "graz" => PickRandom("the Styrian capital", "Murinsel vibes", "design city"),
            "linz" => PickRandom("city of culture", "Ars Electronica territory", "Danube views"),
            "salzburg" => PickRandom("Mozart's hometown", "Sound of Music territory", "Festspielstadt"),
            "innsbruck" => PickRandom("Alpine capital", "Olympic city", "between the mountains"),
            "klagenfurt" => PickRandom("Wörthersee nearby", "the Carinthian capital", "lake country"),
            "villach" => PickRandom("Draustadt vibes", "Carinthian charm", "thermal spa city"),
            "wels" => PickRandom("Upper Austrian vibes", "trade fair city"),
            "st. pölten" or "sankt pölten" => PickRandom("Lower Austrian capital", "the new old town"),
            "dornbirn" => PickRandom("Vorarlberg's largest", "textile city history"),
            "bregenz" => PickRandom("Lake Constance vibes", "Festspielhaus territory", "Bodensee charm"),
            "eisenstadt" => PickRandom("Burgenland capital", "Haydn's city", "wine country"),
            "wiener neustadt" => PickRandom("historic Neustadt", "between Vienna and Alps"),
            "steyr" => PickRandom("confluence city", "industrial heritage"),
            "krems" => PickRandom("Wachau gateway", "wine and art"),
            "baden" => PickRandom("spa town vibes", "thermal springs", "near Vienna"),
            "mödling" => PickRandom("wine region", "near the Wienerwald"),
            "hallstatt" => PickRandom("UNESCO beauty", "the famous village", "picture-perfect"),
            "zell am see" => PickRandom("alpine paradise", "lake and mountains"),
            "kitzbühel" => PickRandom("ski town famous", "Hahnenkamm territory", "Alpine chic"),
            "bad ischl" => PickRandom("imperial summer retreat", "Salzkammergut heart"),
            _ => null
        };
    }

    /// <summary>
    /// Gets a fun vibe for Austrian states (Bundesländer).
    /// </summary>
    private static string? GetAustrianStateVibe(string? state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        
        return state.ToLowerInvariant() switch
        {
            "niederösterreich" or "lower austria" => PickRandom("wine country", "Vienna's backyard", "rolling hills"),
            "oberösterreich" or "upper austria" => PickRandom("industrial heart", "lake region", "Mühlviertel vibes"),
            "steiermark" or "styria" => PickRandom("the green heart", "pumpkin country", "wine and mountains"),
            "kärnten" or "carinthia" => PickRandom("lake country", "sunny south", "Italian-Austrian flair"),
            "salzburg" => PickRandom("Mozart territory", "Alpine charm", "Festspielland"),
            "tirol" or "tyrol" => PickRandom("mountain paradise", "ski country", "Alpine heart"),
            "vorarlberg" => PickRandom("Alemannic corner", "Lake Constance region", "westernmost Austria"),
            "burgenland" => PickRandom("wine country", "Neusiedlersee region", "sunny eastern frontier"),
            _ => null
        };
    }

    /// <summary>
    /// Checks if the country is Austria.
    /// </summary>
    private static bool IsAustria(string? country)
    {
        if (string.IsNullOrEmpty(country)) return false;
        string normalized = country.ToLowerInvariant();
        return normalized is "austria" or "österreich" or "at";
    }

    private static readonly string[] AustriaGenericPhrases =
    [
        "Somewhere in Austria",
        "In beautiful Austria",
        "Exploring Austria",
        "In the Alpine republic",
        "Roaming Austria"
    ];

    /// <summary>
    /// Creates generic location text for non-Vienna, non-Austria locations.
    /// </summary>
    private static string CreateGenericLocationText(string? district, string? locality, string? state, string? country)
    {
        List<string> parts = new List<string>();

        // Build location hierarchy
        if (!string.IsNullOrEmpty(district) && district != locality)
        {
            parts.Add(district);
        }

        if (!string.IsNullOrEmpty(locality))
        {
            parts.Add(locality);
        }

        // Add country context for international flavor
        if (!string.IsNullOrEmpty(country) && parts.Count > 0)
        {
            string countryContext = GetCountryContext(country);
            if (!string.IsNullOrEmpty(countryContext))
            {
                return PickRandom(
                    $"In {string.Join(", ", parts)}, {countryContext}",
                    $"Currently in {string.Join(", ", parts)}",
                    $"Visiting {string.Join(", ", parts)}, {countryContext}");
            }
            return PickRandom(
                $"In {string.Join(", ", parts)}",
                $"Somewhere in {string.Join(", ", parts)}");
        }

        if (parts.Count > 0)
        {
            return PickRandom(
                $"In {string.Join(", ", parts)}",
                $"Currently in {string.Join(", ", parts)}",
                $"Somewhere in {string.Join(", ", parts)}");
        }

        // Fallback to state/country
        if (!string.IsNullOrEmpty(state))
        {
            if (!string.IsNullOrEmpty(country))
            {
                return PickRandom(
                    $"Somewhere in {state}, {country}",
                    $"In {state}, {country}",
                    $"Roaming {state}");
            }
            return PickRandom(
                $"Somewhere in {state}",
                $"In {state}",
                $"Exploring {state}");
        }

        if (!string.IsNullOrEmpty(country))
        {
            return PickRandom(
                $"Somewhere in {country}",
                $"In {country}",
                $"Visiting {country}");
        }

        return PickRandom(
            "Somewhere in the world",
            "Location unknown",
            "Off the grid",
            "Out exploring");
    }

    /// <summary>
    /// Returns a friendly context string for a country, or empty if it should be omitted.
    /// </summary>
    private static string GetCountryContext(string country)
    {
        string normalizedCountry = country.ToLowerInvariant();
        
        return normalizedCountry switch
        {
            "austria" or "österreich" => "Austria",
            "germany" or "deutschland" => "Germany",
            "switzerland" or "schweiz" or "suisse" => "Switzerland",
            "united states" or "united states of america" or "usa" => "USA",
            "united kingdom" or "uk" => "UK",
            "czech republic" or "czechia" or "česko" => "Czechia",
            "hungary" or "magyarország" => "Hungary",
            "slovakia" or "slovensko" => "Slovakia",
            "italy" or "italia" => "Italy",
            "france" => "France",
            "spain" or "españa" => "Spain",
            "netherlands" or "nederland" => "Netherlands",
            _ => country // Return as-is for other countries
        };
    }

    #endregion

    #region Vienna District Handling

    /// <summary>
    /// Creates location text specifically for Vienna with district handling.
    /// </summary>
    private static string CreateViennaLocationText(string? districtName)
    {
        if (!string.IsNullOrEmpty(districtName))
        {
            int? districtNumber = ExtractViennaDistrictNumber(districtName);
            if (districtNumber.HasValue)
            {
                string? friendlyName = GetViennaDistrictFriendlyName(districtNumber.Value);
                string? vibe = GetViennaDistrictVibe(districtNumber.Value);
                
                // Use varied phrase patterns for Vienna
                return PickRandom(GenerateViennaPhrase(districtNumber.Value, friendlyName, vibe));
            }
            return $"In {districtName}, Vienna";
        }
        return PickRandom(ViennaGenericPhrases);
    }

    /// <summary>
    /// Generates varied phrases for Vienna locations.
    /// </summary>
    private static string[] GenerateViennaPhrase(int districtNumber, string? friendlyName, string? vibe)
    {
        string ordinal = GetOrdinal(districtNumber);
        List<string> phrases = new();

        if (!string.IsNullOrEmpty(friendlyName))
        {
            phrases.Add($"In {friendlyName}, the {ordinal} district of Vienna");
            phrases.Add($"Somewhere in {friendlyName}");
            phrases.Add($"Hanging out in {friendlyName}");
            phrases.Add($"Chilling in the {ordinal}");
            phrases.Add($"In Vienna's {friendlyName}");
            
            if (!string.IsNullOrEmpty(vibe))
            {
                phrases.Add($"In {friendlyName} – {vibe}");
                phrases.Add($"Enjoying {friendlyName} – {vibe}");
            }
        }
        else
        {
            phrases.Add($"In the {ordinal} district of Vienna");
            phrases.Add($"Somewhere in Vienna's {ordinal}");
        }

        return phrases.ToArray();
    }

    /// <summary>
    /// Gets a fun vibe/description for Vienna districts.
    /// </summary>
    private static string? GetViennaDistrictVibe(int districtNumber)
    {
        return districtNumber switch
        {
            1 => PickRandom("the heart of Vienna", "where the tourists roam", "Stephansdom territory", "imperial vibes"),
            2 => PickRandom("Prater territory", "home of the Riesenrad", "the lively second", "Karmelitermarkt vibes"),
            3 => PickRandom("embassy district", "Belvedere neighborhood", "the calm third"),
            4 => PickRandom("compact and cozy", "Naschmarkt adjacent", "the petite fourth"),
            5 => PickRandom("the creative fifth", "Reinprechtsdorfer Straße energy", "up-and-coming Margareten"),
            6 => PickRandom("shopping paradise", "Mariahilfer Straße central", "bustling Mariahilf"),
            7 => PickRandom("hipster central", "the trendy seventh", "Neubau vibes", "bobo territory"),
            8 => PickRandom("the elegant eighth", "smallest but mighty", "cozy Josefstadt", "Theater district"),
            9 => PickRandom("student district", "near the Uni", "the academic ninth", "Alsergrund atmosphere"),
            10 => PickRandom("the big tenth", "diverse Favoriten", "Viktor-Adler-Markt territory", "Wien Hauptbahnhof neighborhood"),
            11 => PickRandom("industrial charm", "Simmeringer vibes", "Zentralfriedhof adjacent"),
            12 => PickRandom("the cozy twelfth", "Schönbrunn adjacent", "laid-back Meidling"),
            13 => PickRandom("Schönbrunn territory", "the fancy thirteenth", "imperial gardens nearby", "posh Hietzing"),
            14 => PickRandom("Hütteldorf vibes", "green Penzing", "Wienerwald adjacent"),
            15 => PickRandom("multicultural fifteen", "Westbahnhof territory", "Fünfhaus energy"),
            16 => PickRandom("real Vienna vibes", "Brunnenmarkt territory", "authentic Ottakring", "Yppenplatz scene"),
            17 => PickRandom("the village in the city", "Hernals heights", "Jörgerbad neighborhood"),
            18 => PickRandom("Türkenschanzpark nearby", "leafy Währing", "the educated eighteenth"),
            19 => PickRandom("the wine district", "Heuriger territory", "fancy Döbling", "Grinzing nearby"),
            20 => PickRandom("Millennium Tower neighborhood", "Brigittenau vibes", "Danube adjacent"),
            21 => PickRandom("across the Danube", "Floridsdorf life", "the northern frontier"),
            22 => PickRandom("Donaustadt expanse", "UNO City territory", "Seestadt vibes", "the big district"),
            23 => PickRandom("the southern edge", "Liesing life", "suburban Vienna"),
            _ => null
        };
    }

    private static readonly string[] ViennaGenericPhrases =
    [
        "Somewhere in Vienna",
        "In Wien",
        "Exploring Vienna",
        "In the beautiful capital",
        "In my favorite city",
        "Somewhere in the Kaiserstadt"
    ];

    /// <summary>
    /// Checks if the address is in Vienna.
    /// </summary>
    private static bool IsVienna(NominatimAddress address)
    {
        string city = address.City ?? address.Town ?? "";
        return city.Equals("Wien", StringComparison.OrdinalIgnoreCase) ||
               city.Equals("Vienna", StringComparison.OrdinalIgnoreCase) ||
               (address.State?.Contains("Wien", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Checks if a city name is Vienna.
    /// </summary>
    private static bool IsViennaCity(string? cityName)
    {
        if (string.IsNullOrEmpty(cityName))
            return false;
            
        return cityName.Equals("Wien", StringComparison.OrdinalIgnoreCase) ||
               cityName.Equals("Vienna", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the district number from a Vienna district name.
    /// </summary>
    private static int? ExtractViennaDistrictNumber(string districtName)
    {
        // Try to extract a number from the district name
        var match = System.Text.RegularExpressions.Regex.Match(districtName, @"(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
        {
            return number;
        }

        // Map common Vienna district names to numbers
        Dictionary<string, int> districtMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Innere Stadt", 1 },
            { "Leopoldstadt", 2 },
            { "Landstraße", 3 },
            { "Wieden", 4 },
            { "Margareten", 5 },
            { "Mariahilf", 6 },
            { "Neubau", 7 },
            { "Josefstadt", 8 },
            { "Alsergrund", 9 },
            { "Favoriten", 10 },
            { "Simmering", 11 },
            { "Meidling", 12 },
            { "Hietzing", 13 },
            { "Penzing", 14 },
            { "Rudolfsheim-Fünfhaus", 15 },
            { "Ottakring", 16 },
            { "Hernals", 17 },
            { "Währing", 18 },
            { "Döbling", 19 },
            { "Brigittenau", 20 },
            { "Floridsdorf", 21 },
            { "Donaustadt", 22 },
            { "Liesing", 23 }
        };

        foreach (KeyValuePair<string, int> kvp in districtMap)
        {
            if (districtName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a friendly name for Vienna districts (the neighborhood name).
    /// </summary>
    private static string? GetViennaDistrictFriendlyName(int districtNumber)
    {
        return districtNumber switch
        {
            1 => "Innere Stadt",
            2 => "Leopoldstadt",
            3 => "Landstraße",
            4 => "Wieden",
            5 => "Margareten",
            6 => "Mariahilf",
            7 => "Neubau",
            8 => "Josefstadt",
            9 => "Alsergrund",
            10 => "Favoriten",
            11 => "Simmering",
            12 => "Meidling",
            13 => "Hietzing",
            14 => "Penzing",
            15 => "Rudolfsheim-Fünfhaus",
            16 => "Ottakring",
            17 => "Hernals",
            18 => "Währing",
            19 => "Döbling",
            20 => "Brigittenau",
            21 => "Floridsdorf",
            22 => "Donaustadt",
            23 => "Liesing",
            _ => null
        };
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Picks a random element from an array of strings.
    /// </summary>
    private static string PickRandom(params string[] options)
    {
        return options.Length == 0 ? string.Empty : options[Random.Next(options.Length)];
    }

    /// <summary>
    /// Gets the ordinal suffix for a number (1st, 2nd, 3rd, etc.)
    /// </summary>
    private static string GetOrdinal(int number)
    {
        if (number <= 0) return number.ToString();

        string suffix = (number % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (number % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            }
        };

        return $"{number}{suffix}";
    }

    #endregion
}


