<?xml version="1.0"?>
<xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:frmwrk="Corel Framework Data">
  <xsl:output method="xml" encoding="UTF-8" indent="yes"/>

  <frmwrk:uiconfig>
    <frmwrk:applicationInfo userConfiguration="true" />
    <frmwrk:compositeNode xPath="/uiConfig/commandBars/commandBarData[@guid='3eaa9bbe-28fd-4672-9128-02974ee96332']"/>
    <frmwrk:compositeNode xPath="/uiConfig/commandBars/commandBarData[@guid='c2b44f69-6dec-444e-a37e-5dbf7ff43dae']"/>
    <frmwrk:compositeNode xPath="/uiConfig/frame"/>
  </frmwrk:uiconfig>

  <xsl:template match="node()|@*">
    <xsl:copy>
      <xsl:apply-templates select="node()|@*"/>
    </xsl:copy>
  </xsl:template>

  <xsl:template match="commandBarData[@guid='3eaa9bbe-28fd-4672-9128-02974ee96332']/menu">
    <xsl:copy>
      <xsl:apply-templates select="node()|@*"/>
      <xsl:if test="not(./item[@guidRef='60cc0805-a158-44eb-a286-49061c3d963d'])">
        <item guidRef="60cc0805-a158-44eb-a286-49061c3d963d"/>
      </xsl:if>
    </xsl:copy>
  </xsl:template>

  <xsl:template match="commandBarData[@guid='c2b44f69-6dec-444e-a37e-5dbf7ff43dae']/toolbar">
    <xsl:copy>
      <xsl:apply-templates select="node()|@*"/>
      <xsl:if test="not(./item[@guidRef='60cc0805-a158-44eb-a286-49061c3d963d'])">
        <item guidRef="60cc0805-a158-44eb-a286-49061c3d963d" itemFace="textOnly"/>
      </xsl:if>
      <xsl:if test="not(./item[@guidRef='a624e7f3-8a23-4b1e-b2e6-58cf6725b9e4'])">
        <item guidRef="a624e7f3-8a23-4b1e-b2e6-58cf6725b9e4"/>
      </xsl:if>
      <xsl:if test="not(./item[@guidRef='96b63d8e-86b4-433f-a315-126381e35e11'])">
        <item guidRef="96b63d8e-86b4-433f-a315-126381e35e11"/>
      </xsl:if>
    </xsl:copy>
  </xsl:template>
</xsl:stylesheet>
