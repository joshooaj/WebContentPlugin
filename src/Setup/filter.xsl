<xsl:stylesheet
    version="1.0" exclude-result-prefixes="xsl wix"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:wix="http://wixtoolset.org/schemas/v4/wxs"
    xmlns="http://wixtoolset.org/schemas/v4/wxs">

    <xsl:output method="xml" indent="yes" omit-xml-declaration="yes" />

    <xsl:strip-space elements="*" />

    <xsl:key name="FilterPdbs"
        match="wix:Component[substring(wix:File/@Source, string-length(wix:File/@Source) -3) = '.pdb']"
        use="@Id" />
    <xsl:key name="FilterVideoOS"
        match="wix:Component[contains(wix:File/@Source, 'VideoOS')]"
        use="@Id" />

    <xsl:template match="@*|node()">
        <xsl:copy>
            <xsl:apply-templates select="@*|node()" />
        </xsl:copy>
    </xsl:template>

    <!-- <xsl:template match="*[ self::wix:Component or self::wix:ComponentRef ][ key( 'FilterPdbs', @Id ) ]" /> -->
    <xsl:template match="*[ self::wix:Component or self::wix:ComponentRef ][ key( 'FilterVideoOS', @Id ) ]" />
</xsl:stylesheet>