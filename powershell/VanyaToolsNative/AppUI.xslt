<?xml version="1.0"?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:frmwrk="Corel Framework Data">
  <xsl:output method="xml" encoding="UTF-8" indent="yes"/>

  <frmwrk:uiconfig>
    <frmwrk:applicationInfo userConfiguration="true" />
  </frmwrk:uiconfig>

  <xsl:template match="node()|@*">
    <xsl:copy>
      <xsl:apply-templates select="node()|@*"/>
    </xsl:copy>
  </xsl:template>

  <xsl:template match="uiConfig/items">
    <xsl:copy>
      <xsl:apply-templates select="node()|@*"/>

      <itemData guid="60cc0805-a158-44eb-a286-49061c3d963d"
                noBmpOnMenu="true"
                type="checkButton"
                check="*Docker('e75f8206-5448-4578-ae13-ee8d9a4f15c9')"
                dynamicCategory="2cc24a3e-fe24-4708-9a74-9c75406eebcd"
                userCaption="Vanya Tools Native"
                enable="true"/>

      <itemData guid="459dee79-83a1-40c9-adba-7d61a6cb8f00"
                type="wpfhost"
                hostedType="Addons\VanyaToolsNative\VanyaTools.Native.dll,VanyaTools.Native.VanyaToolsDocker"
                enable="true" />
    </xsl:copy>
  </xsl:template>

  <xsl:template match="uiConfig/dockers">
    <xsl:copy>
      <xsl:apply-templates select="node()|@*"/>

      <dockerData guid="e75f8206-5448-4578-ae13-ee8d9a4f15c9"
                  userCaption="Vanya Tools Native"
                  wantReturn="true"
                  focusStyle="noThrow">
        <container>
          <item dock="fill" margin="0,0,0,0" guidRef="459dee79-83a1-40c9-adba-7d61a6cb8f00"/>
        </container>
      </dockerData>
    </xsl:copy>
  </xsl:template>
</xsl:stylesheet>
